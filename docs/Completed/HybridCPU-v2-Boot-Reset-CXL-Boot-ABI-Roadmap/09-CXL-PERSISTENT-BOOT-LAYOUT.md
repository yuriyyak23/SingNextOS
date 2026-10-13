# CXL Persistent Boot Layout

## 1. Decision: compact native format, not GPT in Stage-0

CXL Type-3 persistent capacity is byte-addressable after HDM mapping. Stage-0 needs a tiny deterministic parser, not partition-table interoperability. v1 therefore defines a compact **Hybrid Boot Volume (HBV)** format relative to the persistent-capacity base returned by the preboot backend.

GPT MAY coexist outside the reserved boot metadata prefix for tooling/OS use, but ROM does not parse GPT in v1.

## 2. Persistent-capacity-relative layout

`P0` means base of the device persistent capacity/partition selected by CXL Identify/partition information, not absolute DPA 0.

```text
P0 + 0x0000_0000  HBV superblock copy 0       4 KiB
P0 + 0x0000_1000  HBV superblock copy 1       4 KiB
P0 + 0x0001_0000  manifest slot A region      64 KiB default
P0 + 0x0002_0000  manifest slot B region      64 KiB default
P0 + 0x0003_0000  recovery manifest region    64 KiB optional
P0 + 0x0004_0000  metadata/update journal     bounded
P0 + ...          reserved metadata
P0 + 0x0100_0000  end of v1 boot anchor       16 MiB
P0 + >=16 MiB     Stage1/kernel/services payload extents
```

Payloads SHOULD be 2 MiB aligned where practical; metadata is 4 KiB aligned. A manifest can place payloads anywhere within persistent capacity if bounds/overflow/alignment checks pass.

## 3. HBV superblock

```c
struct HybridBootVolumeHeaderV1 {
    uint32_t magic;                 // 'HBV1'
    uint16_t version;               // 1
    uint16_t headerSize;            // 4096 in v1
    uint64_t metadataSequence;
    Guid     bootVolumeId;
    Guid     replicaId;
    uint64_t persistentCapacityBytes;
    uint64_t manifestAOffset;
    uint32_t manifestAMaxBytes;
    uint32_t manifestAFlags;
    uint64_t manifestBOffset;
    uint32_t manifestBMaxBytes;
    uint32_t manifestBFlags;
    uint64_t recoveryManifestOffset;
    uint32_t recoveryManifestMaxBytes;
    uint32_t flags;
    uint8_t  reserved[...];
    uint32_t crc32c;                // last field / covered structural bytes
};
```

This header is **not trusted for security**. CRC, duplicate copies and sequence establish crash/corruption handling; the signed manifest rebinds BootVolume/Replica/Image identities and every executable payload.

## 4. Header copy selection

1. read copy 0/1;
2. validate magic/version/headerSize/CRC/bounds;
3. require capacity values not to exceed actual device persistent capacity;
4. choose highest valid `metadataSequence`;
5. if equal sequence but differing semantic fields → `AmbiguousVolumeHeader`, reject candidate;
6. require target BootVolumeId match before expensive payload reads.

## 5. Manifest storage

Each manifest region contains exactly one `SingNextBootManifest` plus zero padding. Maximum signed manifest size v1 = 64 KiB. Stage-0 reads header first, validates total size <= region max, then reads exact size.

A/B are image slots, not two copies of the same metadata. Redundancy within a slot may be added by storing two manifest records in its 64 KiB region; v1 tooling can use record 0/1 with sequence. The BootState in protected NVRAM still decides trial/confirmed semantics.

## 6. Payload layout

Manifest component descriptors use offsets relative to `P0`, not HPA/DPA. This keeps the on-media format independent from current decoder mapping.

```c
struct BootExtentV1 {
    uint64_t persistentOffset;
    uint64_t storedBytes;
};
```

Stage-0/Stage-1 translates an extent into temporary mapping windows. No on-media HPA/DPA is valid.

## 7. LSA layout

LSA is optional. If the device/platform has explicitly reserved an application label namespace, place one `HybridBootLocatorRecordV1` there. Do not put:

- active slot/attempt counter;
- rollback floor;
- trust key/private key;
- authoritative manifest signature state;
- HPA/DPA/decoder route;
- SingNextOS region/capability tokens;
- writable kernel command line with security authority.

The LSA locator may be stale. Canonical header/manifest wins.

## 8. Why not store the whole manifest in LSA

- LSA may be small or managed by namespace tooling;
- boot payload layout belongs with the persistent capacity it describes;
- replacement/replication tooling can copy volume metadata without requiring identical LSA support;
- signed manifest may grow; keeping ROM LSA reads tiny reduces mailbox attack surface;
- QEMU exposes LSA separately, useful for testing optional behavior rather than making it mandatory.

## 9. Atomic update protocol

SingNextOS updater writes inactive slot only:

```text
1. write payload extents for new image
2. persist/fence payload bytes
3. write manifest record copy 0
4. persist/fence
5. write manifest record copy 1 (or alternate sequence)
6. persist/fence
7. update HBV inactive-slot descriptor/metadata sequence in alternate superblock
8. persist/fence
9. verify by re-read/hash
10. protected BootState.SetTrial(ImageId, Generation, attempts=N)
```

Power loss before step 10 leaves a valid but unselected image. Power loss during header update leaves at least one prior valid header. Trial authority comes only from protected BootState.

## 10. Persistence boundary

The updater must call a platform/provider persistence service that means “durable in the declared persistence domain”. It must not assume CPU cache flush semantics that HybridCPU-v2 has not yet frozen. This is one of the hardware-profile open questions in [23-OPEN-QUESTIONS.md](23-OPEN-QUESTIONS.md).

Stage-0 is read-mostly; it never needs to persist normal slot metadata. It writes only protected boot failure/attempt state through the platform store.

## 11. A/B and recovery slots

- A/B share `BootVolumeId` and `ReplicaId` on a replica.
- each image has unique `ImageId` and generation;
- recovery may live on the CXL volume, but **local recovery outside CXL is still mandatory** to avoid a CXL dead end;
- CXL recovery manifest uses recovery signing policy and may have separate rollback domain.

## 12. Versioning

Unknown HBV major version fails closed for that candidate. Minor-compatible extension uses `headerSize`, `flags`, reserved zeros and TLV areas after fixed header. Stage-0 never guesses semantics of unknown critical flags.

## 13. Alignment and arithmetic invariants

- all metadata offsets: 4 KiB aligned;
- executable payload source offsets SHOULD be >= 4 KiB aligned, 2 MiB recommended;
- offset + size checked for 64-bit overflow;
- extent must be wholly inside actual persistent capacity;
- payload must not overlap boot metadata unless descriptor explicitly names metadata component type (v1 disallows executable overlap);
- component extents may not overlap one another if either is mutable/decompressed to same destination.

## 14. Replica copy rules

A replica operation intentionally copies `BootVolumeId`, image manifests and payloads but writes a **new `ReplicaId`** and resigns/rebuilds the replica-bound header/manifest fields according to tooling policy. If manifest signature binds ReplicaId, signer/update service must issue replica manifest; if v1 wants byte-identical manifests across replicas, ReplicaId belongs only in superblock plus a signed `ReplicaSetId`. This roadmap chooses the simpler v1 rule: manifest contains and signs `ReplicaId`.
