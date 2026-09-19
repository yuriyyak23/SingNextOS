# HybridCPU Reset ABI

## 1. Goal

Define the first architecturally observable state after reset so simulator, firmware, SingNextOS and future hardware do not depend on harness accidents.

## 2. Reset vector and ROM window

**HybridCPU Boot Platform Profile v1**:

```text
ROM_BASE          = 0x0000_0000_FFFC_0000
ROM_SIZE          = 0x0000_0000_0004_0000   # 256 KiB
ROM_END           = 0x0000_0000_FFFF_FFFF
RESET_VECTOR      = ROM_BASE
BUNDLE_ALIGNMENT  = 256 bytes
```

`RESET_VECTOR % 256 == 0` is mandatory because the current native fetch unit consumes 256-byte bundles.

The address is profile-level, not a new ISA opcode. A future incompatible platform profile may define another ROM region only with a different `PlatformId`/Reset ABI major.

## 3. Reset state

After `ArchitecturalReset` completes and before the first fetch:

| State | v1 requirement |
|---|---|
| PC | `RESET_VECTOR` |
| x0 | 0 and immutable by normal writes |
| x1..x31 | 0 for boot VT; zero/canonical for parked VTs |
| instruction address mode | bare physical |
| data address mode | bare physical |
| stack | undefined by hardware; Stage-0 sets it into BootScratch before calls |
| interrupt enable | disabled/masked |
| pending interrupts | cleared/held; reset-cause latch separate |
| trap target | platform boot fault path/ROM-safe default until Stage-0 initializes normal vector |
| caches | invalid/empty or disabled; no dirty data may survive as architectural state |
| TLB/translation cache | invalid |
| IF/ID/EX/MEM/WB | empty |
| replay/loop buffers | invalid/empty, generation reset |
| rename/commit maps | canonical architectural mapping |
| free list | canonical reset population |
| retire queues | empty |
| memory requests | no pre-reset outstanding request may publish after reset |
| DMA/assist/external pending effects | quiesced/cancelled before reset completion or fenced from publication |
| active context | core0/VT0 boot context only |
| other contexts | held in reset or deterministic parked state |
| execution domain | `PlatformFirmwareExecutionDomain` |

No reset transition creates a retire record.

## 4. Reset causes

```c
// Wire format: little-endian u32 in BootInfo.
enum HybridResetReason : uint32_t {
    ColdPowerOn        = 0,
    WarmSoftware       = 1,
    Watchdog           = 2,
    CrashRecovery      = 3,
    FirmwareUpdate     = 4,
    BootTrialFailure   = 5,
    PlatformRecovery   = 6,
    ExternalReset      = 7,
    Unknown            = 0xFFFF_FFFF
};
```

A platform reset-status latch also carries `ResetSequence` (monotonic only within protected platform state if available) and optional diagnostic subreason. Diagnostic subreason never changes boot authority.

## 5. Cold reset

Cold reset MUST:

- establish all state above;
- initialize/validate BootScratch;
- reset platform PCI/CXL hierarchy according to hardware policy, or mark all preserved state untrusted;
- clear volatile BootInfo/stage buffers;
- preserve only defined OTP/protected NVRAM/boot logs.

## 6. Warm reset

Warm reset MAY preserve:

- DRAM contents not marked secret/reset-required;
- PCIe link state;
- CXL link and decoder register contents;
- device persistent media;
- protected BootState.

But Stage-0 MUST NOT assume any preserved CXL mapping is valid. It either clears/reprograms or revalidates all path components before memory reads.

## 7. Watchdog/crash reset

Watchdog and crash reset add two requirements:

1. increment the current trial attempt failure state before selecting the next image if the previous boot was an unconfirmed trial;
2. preserve a bounded crash/reset reason record in protected boot log when available.

No stale SingNextOS provider generation survives.

## 8. Recovery reset

`PlatformRecovery` may be asserted by strap, signed boot policy, repeated boot failure or management controller. Stage-0 then skips normal CXL targets according to policy and enters signed local recovery. Production lock still requires a valid recovery signature unless an explicitly fused manufacturing mode is active.

## 9. Firmware update reset

ROM itself is immutable in v1. “Firmware update” means update of provisionable Stage-1/local recovery/trust store/policy, not rewriting ROM. If future silicon supports A/B ROM/flash, the immutable first-stage verification root must remain outside the updatable bank.

## 10. Reset controller contract

Reference interface:

```csharp
public interface IHybridResetController
{
    HybridResetSnapshot AssertReset(HybridResetReason reason);
    void EstablishArchitecturalResetState(HybridResetSnapshot snapshot);
    void ReleaseBootContext();
    void ParkSecondaryContexts();
}
```

`EstablishArchitecturalResetState()` is atomic with respect to pipeline cycles: `ExecutePipelineCycle()` cannot run between partial sub-resets.

## 11. Reset ordering

```text
stop issue/fetch
 -> block new platform requests
 -> quiesce or poison old completions
 -> clear retire publication
 -> clear pipeline/replay/backend transient state
 -> restore architectural registers/maps
 -> reset translation/cache state
 -> set PC = RESET_VECTOR
 -> latch reset cause
 -> release boot context
```

For a forced/watchdog reset that cannot drain hardware, generations/request epochs MUST change so late completions are ignored.

## 12. Entry ABI to ROM

The ROM entry point receives no required register arguments. It reads reset cause/platform identity from protected platform registers/services. This keeps the reset vector independent from register conventions and guarantees that all writable registers can reset to zero.

Stage-0 must set:

- its stack pointer according to the normal compiler ABI used to build ROM;
- global/thread pointers if that compiler ABI requires them;
- boot exception/fault handler;
- scratch allocator.

These are implementation concerns after reset, not reset-state exceptions.

## 13. Privilege/execution-domain mapping

`PlatformFirmwareExecutionDomain` is a platform state with access to:

- ROM execute/read;
- BootScratch R/W;
- protected boot services;
- PCI config/MMIO required for boot;
- temporary aperture programming.

It cannot be entered by normal SingNextOS/application instruction sequences. v1 simulator may represent it as host/runtime mode. Future hardware maps it to the highest suitable existing privilege mechanism. This does not require a CXL-specific ISA privilege level.

## 14. ISA change decision

**No CXL-specific ISA change.** Required boot semantics are reset-state definitions and a platform address space. If implementation discovers there is literally no architecturally valid way to mask interrupts/set a stack/perform normal physical memory access at ROM entry, that is a generic reset/privileged-execution gap and must be solved generically — not with CXL instructions.
