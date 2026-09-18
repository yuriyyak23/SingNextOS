# Boot / Reset Architecture

## 1. Layering

```text
+--------------------------------------------------------------------+
| SingNextOS runtime                                                  |
| RegionAuthority / OwnedRegion / RegionUse / CXL providers / fabric |
+-----------------------------^--------------------------------------+
                              | fresh admission only
+-----------------------------|--------------------------------------+
| HybridBootInfo handoff       | identity/evidence/temp-state         |
+-----------------------------^--------------------------------------+
| Stage-1 in system RAM        | payload loading, decompression, map   |
+-----------------------------^--------------------------------------+
| Stage-0 immutable Boot ROM   | policy, discovery, verification       |
+-----------------------------^--------------------------------------+
| HybridCPU platform           | reset, ROM, scratch, PCI/MMIO, HDM   |
+--------------------------------------------------------------------+
| CXL/PCIe topology + Type-3 persistent capacity                      |
+--------------------------------------------------------------------+
```

The vertical boundary at kernel entry is explicit: no provider lease/capability crosses it from firmware.

## 2. Boot states

```text
ResetAsserted
  -> ArchitecturalReset
  -> RomEntry
  -> LocalPlatformReady
  -> PolicyLoaded
  -> CxlDiscovery
  -> CandidateSelection
  -> TemporaryMap
  -> ManifestVerify
  -> Stage1CopyVerify
  -> Stage1Running
  -> PayloadLoadVerify
  -> BootInfoSealed
  -> KernelEntry
  -> FirmwareMappingDisposable
  -> OsFreshDiscovery
  -> OsProviderAdmitted
  -> FirmwareMappingReleased
```

Failure edges from every pre-kernel state go to `TryNextCandidate`, `TryNextTarget`, `LocalRecovery`, `RomMonitor`, or `Halt`, as detailed in [16-FAILURE-MODEL.md](16-FAILURE-MODEL.md).

## 3. Reset is not a boot target selection

Reset logic has one job: establish deterministic machine state and transfer control to the reset vector. It does not inspect CXL, choose OS, parse manifests or verify signatures. Those belong to ROM Stage-0.

This separation permits the same reset semantics to boot from local recovery or future non-CXL media without modifying CPU reset logic.

## 4. CPU/platform boundary

CPU reset hardware/model MUST provide:

```text
ResetCauseLatch
ResetVector
ROM fetchability
BootScratch availability
physical/bare addressing
one boot context runnable
platform firmware access domain
```

Platform Stage-0 then obtains boot services. PCIe/CXL protocol details are not CPU architectural registers.

## 5. Minimum pre-OS CXL substrate

Stage-0 CXL support is intentionally narrower than SingNextOS:

- enumerate only roots/devices needed by BootPolicy and a bounded candidate budget;
- identify Type-3 persistent-memory capability;
- collect BDF/DSN/vendor/device/capability evidence;
- use CXL.io register/mailbox transport needed for Identify, partition/capacity and optional GET_LSA;
- program a non-interleaved temporary memory path;
- read boot metadata/payload bytes;
- report errors.

Excluded: allocation API, `OwnedRegion`, pooling policy, general hotplug daemon, multi-host arbitration, application capabilities, long-lived fabric state, runtime reconfiguration loops.

## 6. Candidate selection sequence

For each `BootTarget` in numeric policy priority (ties broken by `TargetId`, not enumeration order):

1. Discover physical endpoints into `BootDeviceEvidence` records.
2. Apply required physical *properties* (Type-3, persistent capacity, security policy) and optional physical selector hints.
3. If LSA locator is available, read bounded locator; reject malformed locator; never trust it cryptographically.
4. Candidates with matching locator `BootVolumeId` can be tried first; candidates without LSA remain eligible.
5. Create narrow mapping to candidate persistent-capacity boot anchor.
6. Read redundant `BootVolumeHeader` copies and choose highest valid metadata sequence whose structural CRC and bounds are valid.
7. Require header `BootVolumeId == target.BootVolumeId`.
8. Load bounded manifest candidates referenced by header; verify signed fields, platform/CPU/FW ABI and rollback floor.
9. Reconcile signed candidates with protected `BootState` (`TrialImageId`, `ConfirmedImageId`).
10. Resolve replicas/conflicts deterministically.
11. Copy+verify Stage-1 and enter it.

## 7. Deterministic multi-device rules

`enumeration order` MUST NOT affect semantic result. Sort/group on semantic records after discovery.

Candidate tuple:

```text
(TargetPriority,
 BootVolumeId,
 RequestedImageState,        # trial before confirmed when explicitly pending
 ImageGeneration,
 ImageId,
 ReplicaHealthClass,
 OptionalPhysicalPreference,
 ReplicaId)
```

Rules:

- if protected state names a valid Trial image, try it first within attempt budget;
- otherwise use the confirmed image exactly named by protected state;
- bootstrap/provisioning mode may select highest valid signed generation and then establish confirmed state;
- same `BootVolumeId + ImageGeneration` with different `ImageId` and no protected choice is **ambiguous split-brain**, not a tie to solve with BDF/DSN;
- same `BootVolumeId + ImageId + generation` on multiple replicas is a legitimate replica set; prefer healthy/non-degraded, then optional DSN policy hint, then lexical `ReplicaId` solely for deterministic I/O choice;
- a changed DSN with unchanged valid BootVolume replica is acceptable unless policy says `RequirePhysicalSelectorMatch`;
- a device reordering never changes target/slot semantics.

## 8. Stage-0 to Stage-1 boundary

Stage-0 verifies only the bounded code necessary to reduce immutable TCB:

```text
signed manifest (ROM verifies signature)
  -> Stage1Descriptor
  -> source bounds
  -> copy to system RAM
  -> hash copied bytes
  -> compare signed hash
  -> W^X/protect destination if platform supports it
  -> jump Stage1Entry
```

Stage-1 receives an internal `Stage0Handoff`, not final `HybridBootInfo`. It includes verified manifest bytes/digest, selected candidate evidence, temporary mapping handle, BootPolicy selection and protected state snapshot.

## 9. Stage-1 to kernel boundary

Stage-1:

- rechecks immutable manifest digest received from Stage-0;
- may window the same firmware boot aperture to payload extents;
- copies/verifies kernel and early services/root payload to normal RAM;
- builds normalized physical memory map;
- validates entry alignment/range;
- constructs `HybridBootInfo` in reserved RAM;
- seals BootInfo checksum/hash;
- flushes instruction visibility as required by current CPU memory model;
- drops `PlatformFirmwareExecutionDomain` to kernel entry state;
- enters kernel using register ABI in [13-HYBRID-BOOT-INFO-ABI.md](13-HYBRID-BOOT-INFO-ABI.md).

## 10. Execute-in-place analysis

### CXL XIP advantages

- avoids large copy;
- can reduce cold boot bandwidth usage;
- may allow demand access to large immutable services.

### CXL XIP costs

- kernel instruction survival depends on CXL link/decoder/fabric state;
- reconfiguration cannot freely invalidate boot mapping;
- instruction fetch faults become CXL RAS/topology faults before provider lifecycle is established;
- caching/coherency/persistence semantics become boot critical;
- TOCTOU between verified bytes and later execution becomes harder;
- SingNextOS cannot discard firmware authority cleanly.

### Decision

v1 MUST copy Stage-1 and kernel executable payloads to local/system RAM. Future `VerifiedCxlXip` MAY be added only as a new manifest payload flag and OS-owned provider binding, never by keeping firmware boot aperture authoritative.

## 11. Reboot semantics

Warm/watchdog/Sing-requested reboot may leave CXL links/decoders electrically configured. Firmware **must treat preserved configuration as stale**:

- no generation survives reset as authority;
- Stage-0 may use preserved link-up state as performance evidence;
- decoder state is either cleared then rebuilt or fully revalidated before use;
- previous BootInfo and OS provider leases are invalid;
- BootState attempt counters/reset reason survive if protected-store policy says so.

A CXL fabric reset is not mandatory for every CPU reset. Correctness depends on revalidation, not “reset everything”.

## 12. Concurrency and SMP

v1 Stage-0 is single-boot-context. Other cores/VTs are held or parked. Stage-1 may release helpers for hashing/decompression only after platform memory and reset semantics are stable; this is an optimization, not ABI. SingNextOS establishes normal topology ownership after entry.

## 13. Performance budgets

Default model limits, configurable per platform profile but bounded:

- maximum 32 boot-relevant endpoints examined;
- maximum 64 KiB LSA/locator bytes per endpoint, v1 locator itself <= 4 KiB;
- config read timeout: 50 ms operation / 250 ms endpoint aggregate;
- mailbox timeout: 500 ms command / 2 s endpoint aggregate;
- metadata mapping/read budget: 16 MiB boot anchor;
- Stage-1 stored size <= 4 MiB, expanded <= 8 MiB in v1 profile;
- total primary target discovery budget default 5 s before next target/recovery.

Real hardware profiles may tune numbers but MUST keep hard upper bounds to resist malicious devices.
