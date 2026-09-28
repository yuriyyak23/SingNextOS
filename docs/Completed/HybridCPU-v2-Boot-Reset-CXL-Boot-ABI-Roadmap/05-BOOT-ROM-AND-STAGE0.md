# Boot ROM and Stage-0

## 1. TCB objective

Stage-0 is the smallest immutable code that can securely turn “CPU reset” into “verified replaceable Stage-1 in normal RAM”. It must not become a general firmware OS.

Target ROM window is 256 KiB; product target for executable+readonly Stage-0 payload is <= 128 KiB, leaving recovery vectors, key tables and growth margin. Size is an engineering budget, not a cryptographic boundary.

## 2. What MUST be in ROM

- reset entry/stack/scratch initialization;
- strict BootPolicy/BootState reader;
- trust-anchor/key-set verification logic;
- bounded PCI config enumeration needed to find CXL boot-capable endpoints;
- minimal CXL capability parser;
- minimal mailbox transport for identity/partition and optional LSA locator;
- temporary HDM boot mapping transaction API/client;
- BootVolumeHeader and Manifest v1 bounded parsers;
- supported hash/signature verification;
- rollback-floor enforcement;
- Stage-1 copy+hash+entry validation;
- boot failure log + target fallback;
- dispatch to local signed recovery / minimal ROM monitor.

## 3. What SHOULD move to Stage-1

- decompression;
- larger payload mapping/windowing;
- full physical memory-map normalization;
- kernel/services relocation;
- richer diagnostics;
- optional parallel hashing;
- optional deeper PCI/CXL topology details needed only for diagnostics;
- construction of final `HybridBootInfo`;
- local recovery UI/console beyond a minimal failure code.

## 4. What MUST remain in SingNextOS

- full `ICxl*` provider lifecycle;
- allocation/placement policy;
- `OwnedRegion` / `RegionUse` / RegionAuthority;
- fabric pooling/reconfiguration;
- device/fabric generations used for normal operation;
- hotplug/hot-remove service;
- normal RAS/poison handling;
- runtime security evidence consumption;
- app/SIP capability projection;
- persistent memory namespace/filesystem policy.

## 5. Stage-0 service decomposition

Keep interfaces narrow:

```csharp
interface IStage0Platform
{
    PlatformIdentity ReadPlatformIdentity();
    HybridResetReason ReadResetReason();
    IProtectedBootStore BootStore { get; }
    IPciConfigAccess Pci { get; }
    ICxlPrebootTransport Cxl { get; }
    ITemporaryBootApertureManager Aperture { get; }
    IBootCrypto Crypto { get; }
    ILocalRecoverySource Recovery { get; }
}
```

Do not define one `IFirmwareServices` object with arbitrary runtime powers.

## 6. Stage-0 state machine

```text
Entry
 -> InitScratch
 -> ValidatePlatformProfile
 -> ReadTrustState
 -> ReadBootPolicy
 -> ReadBootState
 -> DiscoverCandidates
 -> ForEachTarget
      -> ForEachCandidate
           -> ReadLocatorOptional
           -> MapBootAnchor
           -> ReadHeader
           -> ReadManifest
           -> VerifyManifest
           -> ResolveBootState
           -> MapStage1Extent
           -> CopyAndHashStage1
           -> UnmapOrRetainWindowForStage1
           -> BuildStage0Handoff
           -> JumpStage1
 -> LocalRecovery
 -> RomMonitorOrHalt
```

Every parser transition checks integer overflow, lengths, alignment, supported version and maximum count before allocation/copy.

## 7. PCIe enumeration minimum

Stage-0 only needs enough PCI semantics to:

- walk platform-declared boot-capable root hierarchy;
- read vendor/device/class/header information;
- traverse bridge buses with a hard depth/device budget;
- find PCIe/CXL DVSEC/ext capabilities required to recognize Type-3;
- read optional PCIe DSN;
- map the CXL component/device register BAR used for mailbox/decoder control as required by platform adapter.

Stage-0 does not need MSI/MSI-X; polling is preferred for simplicity. Bus mastering remains disabled unless a platform-specific boot transport proves it is necessary.

## 8. CXL mailbox minimum

Abstract commands, not numeric opcodes, are exposed to Stage-0 core:

```csharp
interface ICxlPrebootTransport
{
    CxlIdentifyResult Identify(CxlEndpointHandle endpoint, Deadline deadline);
    CxlPartitionInfo GetPartitionInfo(CxlEndpointHandle endpoint, Deadline deadline);
    Result ReadLsa(CxlEndpointHandle endpoint, uint offset, Span<byte> dst, Deadline deadline);
    CxlDecoderCapabilities QueryDecoderCapabilities(CxlEndpointHandle endpoint);
}
```

Backend maps these to the actual CCI/mailbox path. `ReadLsa` returning Unsupported is normal and must not make the device unbootable.

## 9. Boot metadata strategy

Stage-0 first uses cheap information:

```text
PCI/CXL config -> physical evidence
             -> optional LSA locator
             -> temporary mapping of canonical boot anchor
             -> signed manifest
```

LSA result only changes probe ordering. It cannot waive the canonical BootVolumeHeader + manifest checks.

## 10. Manifest crypto in ROM

ROM supports a small compiled-in algorithm registry. Algorithm identifiers in the manifest select only among algorithms compiled into this ROM ABI; they never load code dynamically.

v1 reference profile:

- content hash: SHA-384;
- signature: Ed25519 for simulator/reference tooling, with ECDSA-P384 reserved as a production profile option;
- key ID: SHA-256/SHA-384 digest of canonical public-key encoding;
- CRC32C only for accidental corruption of unsigned structural metadata.

Algorithm agility is versioned; “unknown algorithm” fails closed.

## 11. TOCTOU rule

Stage-0 MUST execute Stage-1 only from its verified RAM copy:

```text
read source -> copy -> hash(destination bytes) -> compare signed hash -> execute destination
```

It must not verify an extent then later execute it directly from CXL. The manifest itself is copied into BootScratch/normal RAM and its signed-region digest is carried to Stage-1.

## 12. DMA containment

Before OS IOMMU/provider setup:

- Stage-0 does not enable generic PCI bus mastering;
- boot Type-3 memory is accessed by host CXL.mem path, not by trusting device DMA into RAM;
- all MMIO/CCI lengths are bounded;
- boot aperture is isolated from BootScratch/ROM/protected NVRAM;
- if the real platform requires DMA for a bootstrap transport, it needs a dedicated bounce buffer and platform IOMMU rule, added as a hardware-profile extension.

## 13. ROM recovery monitor

The immutable monitor is intentionally primitive:

- print/read a boot status code via platform debug/serial transport;
- expose signed local-recovery selection;
- optionally accept a signed recovery capsule from a physically authorized debug path in manufacturing/service mode;
- never include a full network/TLS/filesystem stack.

Network recovery, if desired, runs from the signed local recovery image.

## 14. Stage-0 handoff structure

Internal only; not cross-project ABI:

```c
struct Stage0HandoffV1 {
    uint32_t magic;                 // 'S0H1'
    uint16_t version;
    uint16_t size;
    Guid     platformId;
    Guid     bootTargetId;
    Guid     bootVolumeId;
    Guid     replicaId;
    Guid     imageId;
    uint64_t imageGeneration;
    uint64_t verifiedManifestAddress;
    uint32_t verifiedManifestSize;
    uint8_t  verifiedManifestDigest[48];
    uint64_t stage1LoadAddress;
    uint64_t stage1Size;
    uint64_t temporaryMappingToken; // firmware-local opaque token
    uint32_t securityFlags;
    uint32_t reserved;
};
```

`temporaryMappingToken` is meaningful only to Stage-1 running in firmware domain; it is never copied as a SingNextOS authority token.
