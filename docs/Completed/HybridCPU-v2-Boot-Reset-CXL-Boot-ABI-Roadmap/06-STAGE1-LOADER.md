# Stage-1 Loader

## 1. Role

Stage-1 is signed/replaceable early firmware code. Its purpose is to keep rich loading logic out of immutable ROM while maintaining the same trust chain.

Stage-1 executes from normal system RAM, never directly from CXL persistent capacity in v1.

## 2. Preconditions

Before entry, Stage-0 has:

- verified manifest signature and rollback generation;
- copied Stage-1 into normal RAM;
- verified Stage-1 destination hash;
- validated entry address/alignment;
- produced `Stage0HandoffV1` in reserved RAM;
- retained a firmware-local temporary mapping service/token or provided a way to remap extents.

Stage-1 MUST reject a malformed handoff or mismatched manifest digest.

## 3. Responsibilities

- normalize local/system RAM map;
- reserve Stage-0/Stage-1/BootInfo/kernel destinations;
- map CXL payload extents through the temporary aperture as needed;
- copy/hash all executable/data components named by signed manifest;
- decompress only after validating stored-byte hash if compression is used, and validate final-image hash if manifest carries both;
- apply bounded relocations only if format permits them;
- ensure executable entry point lands inside a verified executable component;
- construct final `HybridBootInfo`;
- close firmware writable/executable mappings where possible;
- call the kernel entry ABI.

## 4. What Stage-1 must NOT do

- establish `OwnedRegion` or SingNextOS capabilities;
- expose firmware decoder handles as OS handles;
- run a persistent CXL fabric manager;
- make BDF/DSN/HPA/DPA semantic IDs;
- write confirmed boot state merely because kernel was entered;
- accept unsigned secondary payloads outside signed manifest policy;
- silently switch to XIP when RAM allocation fails.

## 5. Component loading algorithm

```text
for component in Manifest.Components in declared dependency order:
    validate descriptor bounds/alignment/type
    allocate destination from allowed RAM class
    while bytes remain:
        aperture.Map(sourcePersistentOffset, boundedWindow)
        read -> destination/staging
        update stored hash
    verify stored hash
    if compressed:
        decompress into final destination with exact output bound
        verify final hash
    enforce W^X / mark executable-readonly where available
record LoadedImageDescriptor
```

Kernel and executable services use at least 256-byte entry alignment for native VLIW bundle fetch. Data-only payloads use descriptor alignment.

## 6. Memory allocation policy

Stage-1 may use only entries classified `UsableSystemRam` by the platform memory map. It excludes:

- ROM;
- BootScratch;
- protected NVRAM/MMIO;
- firmware-reserved regions;
- boot aperture;
- already loaded components;
- BootInfo/log buffers.

The allocator is deterministic first-fit-by-address in v1 to make simulator replay/debugging stable. ASLR can be a future kernel concern unless a later secure-boot profile adds signed relocation support.

## 7. Compression

Compression is optional and Stage-1-only. Stage-0 never decompresses its own executable.

v1 allows:

```c
enum BootCompression : uint16_t {
    None = 0,
    Lz4Block = 1,    // optional profile feature
    Zstd = 2         // future/profile-gated
};
```

A platform advertises supported set in `FirmwareBootAbiFeatures`; manifest requiring unsupported compression fails before partial execution.

## 8. Payload integrity semantics

Recommended component descriptor carries:

- hash of stored bytes (`StoredHash`) — detects source corruption before decompression;
- hash of final loaded bytes (`LoadedHash`) — binds actual executable/data image;
- exact stored/final lengths.

A component is “verified” only after final loaded hash matches.

## 9. Kernel entry validation

Before jump:

- `KernelEntry` belongs to exactly one verified executable component;
- address is 256-byte aligned;
- target range is executable/read-only if platform permissions exist;
- no overlap with BootInfo/stack/aperture/firmware scratch;
- instruction-visibility synchronization required by CPU model is performed;
- BootInfo checksum/hash is final;
- firmware has blocked further mutation of BootInfo.

## 10. Stage-1 failure behavior

Stage-1 failure returns through a narrow non-returning Stage-0 recovery gate or platform reset request. It does not jump to another unverified image itself without updating the protected attempt record.

Reference path:

```text
Stage1 failure
 -> write BootFailureRecord(reason, image, component)
 -> MarkTrialAttemptFailed if applicable
 -> RequestReset(BootTrialFailure)
 -> Stage0 selection/fallback
```

For deterministic simulator tests Stage-1 may return a typed failure to harness; product contract treats kernel entry/recovery as non-returning.

## 11. Firmware runtime after handoff

There is no resident general Stage-1 firmware runtime. The only allowed post-handoff platform control surface is a minimal boot-control/reset service implemented by platform registers/trap/host call as appropriate:

```text
ConfirmBoot(ImageId, Generation)
SetNextBootTarget(TargetId, optional ImageId)
RequestReset(Reason)
QueryResetReason / QueryBootSecurityState
```

None of these services manages CXL decoders or regions for the OS. SingNextOS uses its providers for that.

## 12. Stage-1 size/performance

Reference limits:

- stored Stage-1 <= 4 MiB;
- loaded Stage-1 <= 8 MiB;
- no dynamic plugin loading;
- manifest component count <= 64;
- component source extent count <= 8 per component in v1;
- all arithmetic uses checked 64-bit offsets.

Limits are encoded in ABI/profile, not inferred from available memory, so malicious manifests cannot force unbounded work.
