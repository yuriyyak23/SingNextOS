# HybridBootInfo ABI

## 1. Purpose

`HybridBootInfo` is the one-way, immutable handoff contract from verified Stage-1 to SingNextOS. It describes the boot result and current machine state. It does **not** carry live CXL authority.

## 2. ABI design

Use a fixed header plus typed variable-length records. All records are 8-byte aligned; unknown non-critical record types are skipped using `size`; unknown critical record types reject boot.

```c
struct HybridBootInfoHeaderV1 {
    uint32_t magic;             // 'HBI1'
    uint16_t abiMajor;          // 1
    uint16_t abiMinor;          // 0
    uint32_t headerSize;
    uint32_t totalSize;         // hard max 1 MiB v1
    uint64_t flags;
    Guid     platformId;
    uint32_t cpuAbiVersion;
    uint32_t firmwareBootAbiVersion;
    uint32_t resetReason;
    uint32_t recordCount;
    uint64_t resetSequence;
    uint8_t  blockDigest[48];   // SHA-384, digest field zeroed while computing
    uint32_t crc32c;
    uint32_t reserved;
};

struct HybridBootInfoRecordHeaderV1 {
    uint16_t type;
    uint16_t flags;             // CRITICAL, EVIDENCE_ONLY, TEMPORARY...
    uint32_t size;
};
```

## 3. Required records

### BootSelection

```c
struct HbiBootSelectionV1 {
    RecordHeader h;
    Guid targetId;
    Guid bootVolumeId;
    Guid replicaId;
    Guid imageId;
    Guid rollbackDomainId;
    uint64_t imageGeneration;
    uint16_t slotKind;
    uint16_t bootAttemptOrdinal;
    uint32_t selectionFlags;    // Trial/Confirmed/Recovery
};
```

Semantic identity, **not authority**.

### SecurityState

```c
struct HbiSecurityStateV1 {
    RecordHeader h;
    uint64_t flags;             // ManifestVerified, ProductionLocked, DevelopmentMode...
    uint64_t rollbackFloor;
    uint64_t trustStoreGeneration;
    uint8_t  signingKeyId[32];
    uint8_t  manifestDigest[48];
};
```

Evidence about verification. It does not authorize OS CXL accesses.

### PhysicalMemoryMap

Array entries:

```c
enum HbiMemoryType : uint32_t {
    UsableSystemRam = 1,
    ImmutableRom = 2,
    FirmwareReserved = 3,
    BootScratch = 4,
    LoadedKernel = 5,
    LoadedBootComponent = 6,
    BootInfo = 7,
    Mmio = 8,
    FirmwareTemporaryCxlAperture = 9,
    Reserved = 0xFFFF
};

struct HbiMemoryRangeV1 {
    uint64_t base;
    uint64_t bytes;
    uint32_t type;
    uint32_t attributes;
};
```

Only `UsableSystemRam` is allocator-eligible before SingNextOS constructs its own memory model.

### LoadedImages

Component type, load base/size, final hash, entry point if any. These are verified-byte evidence and boot ownership of local RAM, not CXL provider authority.

### BootDeviceEvidence

```c
struct HbiCxlBootDeviceEvidenceV1 {
    RecordHeader h;                   // EVIDENCE_ONLY
    uint16_t pciSegment;
    uint8_t bus, device, function;
    uint16_t vendorId, pciDeviceId;
    uint64_t pciDsn;                  // optional
    uint64_t cxlSerial;               // optional
    uint32_t cxlRevision;
    uint32_t capabilityBits;
    Guid     bootVolumeId;
    Guid     replicaId;
    uint8_t  locatorDigest[32];
    uint8_t  topologyDigest[32];
};
```

BDF/DSN are diagnostics/evidence only.

### TemporaryBootMapping

Describes HPA range and state, marked `TEMPORARY | EVIDENCE_ONLY | MUST_NOT_USE_AS_RAM`. No control token is passed.

### CpuTopology

Initial core/VT topology discovered by platform. Secondary contexts are parked. SingNextOS may re-enumerate/validate before release.

### BootLog

Bounded immutable ring snapshot with typed failure codes. Sensitive raw protocol dumps are excluded from normal BootInfo; privileged diagnostics can query later.

## 4. Authority classification table

| BootInfo field | Class | SingNextOS treatment |
|---|---|---|
| BootVolumeId/ImageId/generation | semantic identity/evidence | record boot provenance; no memory right |
| manifest/key hashes | security evidence | validate/attest state |
| memory map UsableSystemRam | handoff resource description | early allocator may consume according to Boot ABI |
| loaded kernel ranges | OS-owned local boot memory | kernel already executing; reserve appropriately |
| DSN/BDF/topology | physical evidence | diagnostics/matching only |
| boot aperture HPA | temporary firmware state | reserve/do not allocate; revalidate/tear down |
| decoder/DPA (optional diag TLV) | provider-private diagnostic | never create capability |
| firmware mapping generation | stale-state evidence | never map to CXL provider generation |
| `OwnedRegion` / provider lease | **not present** | created fresh by OS |

## 5. Register-level kernel entry ABI

HybridCPU exposes 32×64-bit x registers. To avoid redefining the language/compiler calling convention, kernel boot entry uses a small dedicated convention based on conventional argument-register positions:

```text
PC  = KernelEntry (256-byte aligned)
x10 = physical pointer to HybridBootInfo
x11 = HybridBootInfo totalSize
x12 = FirmwareBootAbiVersion
x13 = boot logical CPU/VT id (v1 = 0)
x14 = HybridResetReason
x15 = HandoffFlags
x0  = 0
```

Stage-1 sets the normal stack register according to the HybridCPU compiler ABI before entry; `HybridBootInfo` never relies on that register number. All other registers are zero unless the normal ABI requires a platform value. SingNextOS entry stub immediately saves/validates these arguments.

Why x10–x15: they avoid inventing new architectural registers/instructions and align with the conventional argument register pattern used by RISC-V-like 32-register toolchains. **The project should freeze this as HybridCPU Boot ABI even if its higher-level C ABI later evolves.**

## 6. Addressing at kernel entry

- BootInfo pointer is a physical address valid under bare/identity early mapping.
- Kernel starts with the address-translation state required by Reset ABI profile (v1 bare); kernel may enable its normal translation later.
- BootInfo/loaded ranges are within directly reachable system RAM.
- temporary CXL aperture is mapped but forbidden for normal allocation/use until provider takeover.

## 7. Handoff flags

Recommended:

```text
BootWasTrial
BootWasRecovery
DevelopmentMode
CxlBootSource
TemporaryCxlMappingEnabled
PreviousResetWasWatchdog
BootLogPresent
MeasuredBootLogPresent
```

Flags describe facts only.

## 8. BootInfo lifecycle

1. Stage-1 allocates reserved pages.
2. Writes records in canonical order.
3. Sorts memory map by base; rejects overlaps except explicit containment descriptors.
4. Computes digest/CRC.
5. Marks pages read-only if platform supports it.
6. Jumps kernel.
7. SingNextOS parses/copies durable provenance it needs.
8. BootInfo pages become reclaimable only after all early consumers complete; boot aperture remains separately reserved until provider takeover.

## 9. Version compatibility

Kernel declares supported Boot ABI range in signed manifest. Stage-1 only enters a kernel supporting its ABI. Minor versions may add skippable records. Any change to register meanings, required reset state or authority semantics is a major version.

## 10. No raw capabilities

Explicitly forbidden BootInfo records:

- “CxlDeviceHandle” usable by normal OS operations;
- firmware decoder write handle;
- `OwnedRegion` token serialized by firmware;
- fabric lease token;
- deviceId/index as boot authority;
- raw pointer to mutable firmware CXL provider object.
