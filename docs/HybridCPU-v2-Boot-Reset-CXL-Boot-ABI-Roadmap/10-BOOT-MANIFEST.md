# SingNext Boot Manifest

## 1. Purpose

The manifest is the canonical signed statement of **what may execute**, **for which platform/ABI**, **from which logical BootVolume/replica**, and **at which rollback generation**.

## 2. v1 wire format

All fixed integers are little-endian. GUIDs use RFC-4122/network byte order inside their 16-byte field; tooling must not use host-language GUID memory layout implicitly.

```c
struct SingNextBootManifestHeaderV1 {
    uint32_t magic;                 // 'SNB1'
    uint16_t formatMajor;           // 1
    uint16_t formatMinor;           // 0
    uint32_t headerSize;
    uint32_t totalSize;             // <= 64 KiB
    uint32_t signedRegionSize;
    uint32_t flags;

    Guid     bootVolumeId;
    Guid     replicaId;
    Guid     imageId;
    Guid     rollbackDomainId;
    uint64_t imageGeneration;

    Guid     platformFamilyId;       // exact or family; zero if compatibility TLV used
    uint32_t requiredCpuAbiMin;
    uint32_t requiredCpuAbiMax;
    uint32_t requiredFirmwareAbiMin;
    uint32_t requiredFirmwareAbiMax;
    uint64_t requiredPlatformFeatures;

    uint16_t slotKind;               // A/B/Recovery/Service
    uint16_t componentCount;         // <= 64
    uint32_t componentTableOffset;
    uint64_t kernelEntryOffsetInComponent;
    uint16_t kernelComponentIndex;
    uint16_t stage1ComponentIndex;
    uint32_t reserved0;

    uint16_t hashAlgorithm;
    uint16_t signatureAlgorithm;
    uint32_t signatureBlockOffset;
    uint8_t  signingKeyId[32];
};
```

Header is followed by component descriptors, compatibility TLVs, optional policy data, then signature block. The signature covers bytes `[0, signedRegionSize)` and all security-critical fields/descriptors.

## 3. Component descriptor

```c
struct BootComponentDescriptorV1 {
    uint16_t type;              // Stage1, Kernel, EarlyService, RootImage, Config
    uint16_t flags;             // Executable, Required, Compressed, ReadOnly...
    uint16_t compression;
    uint16_t extentCount;       // <= 8
    uint64_t storedBytes;
    uint64_t loadedBytes;
    uint64_t requiredAlignment;
    uint64_t preferredLoadAddress; // 0 => allocate; not an authority to overwrite memory
    uint64_t entryOffset;       // valid for executable components
    uint8_t  storedHash[48];
    uint8_t  loadedHash[48];
    BootExtentV1 extents[8];
};
```

Unused extents are zero. `preferredLoadAddress` is accepted only if it lies wholly inside usable system RAM and does not overlap reserved ranges; otherwise loader allocates or fails according to a critical flag.

## 4. Semantics

### `BootVolumeId`

Must equal the BootPolicy target and selected HBV header. Semantic locator, not capability.

### `ReplicaId`

Identifies this volume replica and allows duplicate BootVolume handling without using DSN.

### `ImageId`

Immutable identity of the signed image set. A rebuild or materially different payload gets a new ID even if generation is unchanged in development.

### `ImageGeneration`

Monotonic rollback order in `RollbackDomainId`. Production signing service must never reuse a lower generation for a newer release.

### ABI ranges

Ranges prevent firmware/kernel incompatibility. `CpuAbi` covers architectural execution/entry assumptions; `FirmwareBootAbi` covers BootInfo/register/reset semantics. CXL revision is **not** coupled directly to these numbers.

### `slotKind`

Descriptive signed image role. It does not select active slot; protected BootState does.

## 5. Platform compatibility

v1 supports an exact `platformFamilyId` plus feature bits. Future TLV can express a list/range. Stage-0 rejects manifests requiring unknown critical feature bits.

Examples of features:

```text
NeedsCxlType3PersistentBoot
NeedsBootInfoV1
NeedsStage1Lz4
NeedsIommuEarlyContainment
NeedsRecoveryControlService
```

Do not encode a required BDF, HDM decoder index or port as platform compatibility.

## 6. Signature block

```c
struct BootSignatureBlockV1 {
    uint16_t algorithm;
    uint16_t signatureLength;
    uint16_t certificateCount;     // v1 normally 0; key selected by KeyId
    uint16_t reserved;
    uint8_t  signature[...];
};
```

A compact trust store is preferred over embedding long certificate chains in every manifest. Future certificate-chain TLV may be added with strict size limits.

## 7. Validation order

To avoid parser/crypto abuse:

1. read fixed header only;
2. validate magic/version/header/total/signed sizes and maxima;
3. validate table offsets/counts/overflows;
4. reject unknown critical flags/algorithms;
5. check BootVolumeId and ABI compatibility;
6. check generation against protected rollback floor;
7. locate trusted `signingKeyId` in current key set and revocation list;
8. verify manifest signature;
9. only then trust component descriptors semantically;
10. validate all extents against actual capacity and destination policy.

Compatibility checks before signature are only early rejection optimization; no security-sensitive action relies on them until signature succeeds.

## 8. Stage-1 verification

ROM verifies:

- manifest signature;
- Stage-1 stored/final hash;
- Stage-1 entry.

Stage-1 verifies:

- manifest digest inherited from ROM;
- kernel/services stored/final hashes;
- kernel entry.

Stage-1 MUST NOT accept a different manifest from CXL after ROM verified one.

## 9. Command line/configuration

Mutable boot arguments are dangerous. v1 rules:

- security-critical kernel configuration is a signed `Config` component;
- local debug override is permitted only under debug fuse/policy and BootInfo records `DevelopmentMode`;
- arbitrary CXL LSA strings cannot become kernel command line.

## 10. Manifest ambiguity

For one BootVolume:

- two valid manifests, same ImageId/generation and identical signed digest: duplicates; okay;
- same ImageId/generation but different signed digest: corruption/signing error; reject image;
- same generation but different ImageId and no protected trial/confirmed selector: split-brain; reject automatic choice;
- higher generation present but not selected as trial/confirmed: do not auto-boot except first-install bootstrap policy.

## 11. Algorithm agility

Algorithm IDs are registry values; acceptance is intersection of manifest request and ROM/profile-supported algorithms. New algorithms require a new ROM profile or already-compiled support. Unknown algorithm never falls back to “hash only”.

## 12. Entry point rules

Stage-1/kernel executable component:

- loaded size > 0;
- `entryOffset < loadedBytes`;
- resulting physical address cannot overflow;
- address aligned to 256-byte VLIW bundle;
- range marked executable after load;
- no entry into CXL aperture in v1.
