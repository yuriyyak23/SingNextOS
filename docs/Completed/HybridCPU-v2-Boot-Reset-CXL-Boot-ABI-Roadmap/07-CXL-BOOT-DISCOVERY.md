# CXL Boot Discovery

## 1. Goal

Find a SingNextOS boot volume on CXL Type-3 persistent memory without treating runtime enumeration identity as semantic OS identity and without requiring full SingNextOS CXL provider before the OS exists.

## 2. Required discovery properties

The algorithm must survive:

- device reorder / changed PCI BDF;
- device replacement;
- missing DSN;
- switch/fabric path changes;
- multiple devices and replicas;
- same logical BootVolume on a replacement endpoint;
- stale/forged locator metadata;
- LSA absence;
- persistent capacity present but not pre-mapped;
- warm reset with stale decoder state.

## 3. Options evaluated

| Option | Stability | Replacement/fabric | Security | ROM complexity | Decision |
|---|---|---|---|---|---|
| A. fixed `deviceId=0` | terrible | breaks on reorder/hotplug | easy spoof/misbinding | low | **reject** |
| B. fixed PCIe DSN | physical-device stable when present | replacement changes identity; DSN optional | evidence only | low | useful optional constraint, **not primary** |
| C. `BootVolumeId` in persistent metadata | logical stable | survives relocation/replacement/replica | protected by signed manifest correlation | medium | **primary semantic identity** |
| D. CXL LSA locator | may survive capacity remap | endpoint-local; availability/namespace ownership varies | untrusted until manifest verification | mailbox parser | **optional accelerator** |
| E. separate firmware-visible metadata region | good if platform standardizes it | hardware-specific | could be protected | high hardware coupling | future profile only |
| F. platform boot directory/table | stable platform policy | must be synchronized on replacement | protected NVRAM can be strong | medium | use for **policy**, not volume truth |
| G. combined policy + logical ID + signed manifest | strong | strong | strong | bounded | **selected** |

## 4. Selected design

```text
Protected BootPolicy
  -> target BootVolumeId (+ optional physical preference)
  -> bounded PCI/CXL discovery
  -> optional LSA HBLR hint
  -> temporary map persistent boot anchor
  -> canonical BootVolumeHeader
  -> signed manifest binding BootVolumeId/ImageId/Generation
```

This is deliberately stronger than “BootVolumeId + DSN + LSA”: DSN and LSA are optional accelerators/evidence. The canonical identity proof comes from the manifest plus protected target policy.

## 5. Why `device ID 0` is forbidden

`deviceId`/enumeration index describes current simulator or bus discovery order. Hotplug, topology changes, new root ports, switch routing, multi-function devices and test fixture reorder can change it. Making it boot identity would silently bind OS semantics to incidental discovery order and contradict SingNextOS provider-private identity rules.

Even if Phase 3 model uses `devices[0]` internally, the public API must enumerate `CxlBootCandidate` records and select by policy/metadata.

## 6. Physical device identity

The discovery layer may collect:

```c
struct PhysicalDeviceEvidenceV1 {
    uint16_t pciSegment;
    uint8_t  bus, device, function;       // current locator only
    uint16_t vendorId, deviceId;
    uint64_t pciDsn;                      // 0 => absent
    uint64_t cxlDeviceSerial;             // if transport exposes it
    uint32_t cxlRevision;
    uint32_t capabilityBits;
    uint8_t  topologyDigest[32];           // diagnostics only
};
```

`pciDsn` is attractive because PCIe defines an 8-byte Device Serial Number extended capability and software can discover its absence. It is never required to equal BootVolume identity.

## 7. LSA suitability

### Why LSA can be read pre-HDM

Real software accesses CXL memory-device mailbox/CCI through PCIe/CXL.io register paths. Linux’s CXL PMEM path obtains LSA data through `GET_LSA` mailbox operations, not by CPU loads through an HDM range. QEMU likewise models LSA as a separate backend for `cxl-type3`. Therefore a pre-OS implementation can, where the platform exposes mailbox access, read a bounded LSA locator before mapping persistent capacity.

### Why LSA is not mandatory

LSA is also used for persistent-memory label/configuration ecosystems and its availability/size/ownership may vary. Boot ROM must not assume it owns all LSA bytes or reinterpret namespace metadata.

Decision:

- define a **reserved application/vendor UUID namespace** only when platform/device provisioning explicitly supports it;
- never overwrite unknown LSA content;
- HBLR is at most 4 KiB and versioned;
- absence, unsupported command, conflicting labels or invalid CRC are `NoLocator`, not device failure;
- canonical BootVolumeHeader/manifest stays in persistent capacity.

## 8. Hybrid Boot Locator Record

```c
struct HybridBootLocatorRecordV1 {
    uint32_t magic;             // 'HBL1'
    uint16_t version;           // 1
    uint16_t size;              // <= 4096
    Guid     bootVolumeId;
    Guid     replicaId;
    uint64_t locatorGeneration; // metadata freshness hint only
    uint64_t bootAnchorOffset;  // v1 normally 0 relative persistent partition
    uint64_t bootAnchorLength;  // v1 16 MiB
    uint32_t flags;
    uint32_t crc32c;
};
```

Not included: signing keys, secrets, active slot, rollback floor, HPA, DPA, decoder IDs, OS capabilities. `locatorGeneration` is not `ImageGeneration` and cannot authorize rollback decisions.

## 9. Discovery API

```csharp
public interface ICxlBootDiscovery
{
    IReadOnlyList<CxlBootCandidateEvidence> Discover(
        BootDiscoveryBudget budget,
        CancellationToken bootDeadline);
}

public readonly record struct CxlBootCandidateEvidence(
    CxlEndpointHandle Endpoint,
    PhysicalDeviceEvidenceV1 Physical,
    CxlPersistentCapacityInfo Persistent,
    Optional<HybridBootLocatorRecordV1> Locator,
    BootDiscoveryHealth Health);
```

`CxlEndpointHandle` is firmware-private and lifetime-bounded. It never appears in `HybridBootInfo` as an authority handle.

## 10. Bounded PCI/CXL enumeration

Stage-0 obtains root descriptors from the platform profile, not by scanning arbitrary 16-bit segment space. It uses:

- max root buses;
- max bridge depth;
- max endpoints (reference 32);
- per-config read deadlines;
- malformed capability-chain loop detection;
- max DVSEC/ext-cap count;
- no dynamic memory proportional to attacker-declared capability lengths.

## 11. Persistent capacity discovery

Before HDM mapping, preboot transport obtains enough information to identify a persistent partition/capacity and its base/size. The product ABI describes the **persistent capacity relative offset** to the boot-anchor mapper; it does not expose DPA as semantic identity.

If hardware requires partition configuration before this information exists, that operation belongs to a platform profile/provisioning flow and is not performed opportunistically by Stage-0.

## 12. Mapping fallback when LSA is absent

```text
for candidate:
  persistent = Identify/GetPartitionInfo
  aperture.Map(candidate, persistentBase + 0, min(16MiB,persistentSize))
  read BootVolumeHeader copies
  if structural + BootVolumeId match:
       continue manifest selection
  else:
       unmap and try next
```

Thus no custom mailbox command is required to discover the logical volume.

## 13. Fabric and MLD

Boot v1 treats a fabric-attached/MLD path as a current route to an endpoint or logical device assigned to this host. The firmware backend may need Fabric Manager/platform assistance to establish that assignment, but Stage-0 does not manage pooling globally.

Any `LD-ID`, switch route or port ID is stored only in `BootDeviceEvidence`. If the route changes after reset, BootVolume selection remains valid provided the same signed logical volume is reachable.

## 14. Hotplug

Stage-0 snapshots discovery for each boot attempt. It does not run an infinite hotplug service. If an endpoint disappears during verification, candidate fails with `DeviceRemoved/ReadFault`; the next replica/target is tried. A platform may rescan once within the bounded boot budget.

## 15. Security notes

A malicious endpoint can:

- claim any DSN/vendor ID;
- forge HBLR;
- return malformed headers;
- timeout mailbox/config reads;
- swap data between reads.

It cannot cause trusted execution if manifest signature/hash/rollback checks are correct. DoS remains possible for a physically attached malicious endpoint, mitigated by bounded deadlines and fallback.
