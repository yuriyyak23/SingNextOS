# QEMU and Real Hardware Evolution Path

## 1. What QEMU can validate now

Upstream QEMU CXL documentation models CXL on PCIe foundations, Type-3 volatile/persistent memory, CXL host bridges/root ports/switches, HDM decoders/interleave, separate LSA backing and Type-3 serial numbers. This makes QEMU useful for validating the **CXL side** of the boot protocol even if HybridCPU ISA is not an upstream QEMU CPU target.

Useful QEMU validations:

- PCI enumeration and CXL Type-3 capability discovery;
- persistent-memory backend content survives VM restarts when file-backed;
- LSA backend and locator bytes;
- multiple Type-3 devices/serials;
- host CXL fixed windows and decoder programming assumptions;
- direct root-port and switch topology;
- reordered devices;
- multi-device/interleave topology rejection by v1 boot path;
- malformed/missing LSA fixture;
- A/B on-media layout and persistence;
- device removal/reconfiguration where QEMU model supports it.

QEMU does **not** prove physical link security, exact hardware RAS, persistence-domain guarantees or real firmware ownership.

## 2. Separate two validation axes

```text
Axis A: HybridCPU execution validation
  HybridCPU ISE executes ROM/Stage1/kernel entry ABI

Axis B: CXL protocol/model validation
  QEMU x86/q35 or other supported host exercises CXL Type3/LSA/HDM fixtures
```

Shared artifacts: HBV images, HBLR bytes, manifests, protected-policy fixture representation, expected selection vectors. This gives useful protocol confidence before a HybridCPU QEMU target exists.

## 3. Adapter architecture

```csharp
IPciConfigAccess
  ModelPciConfigAccess
  QemuFixturePciAccess / test bridge
  RealEcamPciConfigAccess

ICxlPrebootTransport
  ModelCxlPrebootTransport
  QemuCxlTransport validation adapter
  RealCciMailboxTransport

ITemporaryBootApertureManager
  ModelHdmRouter
  Qemu decoder test harness
  RealCxlDecoderManager
```

No higher-level ABI changes when swapping adapters.

## 4. QEMU fixture example concept

QEMU supports `cxl-type3` with file-backed `persistent-memdev`, separate `lsa` and `sn`. Test harness prepares:

```text
cxl-pmem-A.raw  = HBV replica A persistent capacity
cxl-lsa-A.raw   = HBLR or empty/invalid locator
cxl-pmem-B.raw  = replica or conflicting image
```

A host-side validator runs the same manifest/HBV parser and discovery-selection vector used by HybridCPU reference tests. Later, if a HybridCPU QEMU target is added, the same media files become end-to-end inputs.

## 5. Evolution phases

### Q0 — shared format fixtures

Generate HBV/manifest/LSA binary fixtures and parse them in HybridCPU tests + host/QEMU tooling.

### Q1 — QEMU single Type-3 PMEM

One persistent backend, one LSA, one serial. Validate anchor bytes visible through configured CXL memory and mailbox/LSA semantics.

### Q2 — multiple devices/reorder

Two+ Type-3 endpoints with unique serials, same/different BootVolume IDs. Demonstrate selection logic independent of BDF/order.

### Q3 — switch/topology

Put endpoint behind CXL switch; ensure route/BDF changes remain evidence only.

### Q4 — decoder/window/fault tests

Exercise decoder configuration/reconfiguration where model allows; inject device absence/restart and validate backend error mapping.

### Q5 — real platform discovery adapter

Implement ECAM/firmware table discovery and CCI mailbox on actual platform or firmware test environment.

### Q6 — real Type-3 persistent boot read

Read/verify HBV from hardware into local RAM; do not yet boot full OS. Validate persistence/reset behavior and timeout/error cases.

### Q7 — end-to-end HybridCPU hardware/FPGA (when available)

Reset ROM → real CXL → Stage1 → SingNextOS BootInfo → fresh provider discovery.

## 6. ACPI/CEDT and non-ACPI platforms

QEMU/x86 commonly exposes CXL host bridge/fixed windows via ACPI CEDT. HybridCPU product ABI should not mandate ACPI. Define:

```csharp
interface IPlatformCxlHostDiscovery
{
    IReadOnlyList<CxlHostRootDescriptor> GetBootCapableRoots();
    IReadOnlyList<PlatformCxlWindow> GetFirmwareUsableWindows();
}
```

Implementations:

- ACPI/CEDT parser;
- device-tree-like future profile;
- immutable platform table for FPGA/SoC;
- simulator config.

## 7. Real firmware ownership questions

Before hardware backend is accepted, establish who may program:

- host-bridge/root-port/switch/endpoint HDM decoders;
- CFMW-like windows;
- persistent partitioning;
- MLD logical-device assignment;
- link reset/retrain;
- security/IDE state.

The adapter may call platform firmware instead of writing registers directly. The higher contract remains `MapSingleTarget()`.

## 8. Persistence validation

Hardware milestone must prove the update persistence primitive under:

- CPU caches;
- ADR/eADR or platform persistence domain equivalent;
- CXL device media/cache behavior;
- power loss and warm reset.

Until then simulator `PersistRange/Fence` is semantic scaffolding only.

## 9. RAS/poison

Real Type-3 memory can report poison/media errors. Stage-0 only needs to map these into bounded read failure classes. SingNextOS runtime provider owns rich poison/RAS lifecycle after boot. QEMU fault models are useful but not hardware proof.

## 10. Security evolution

Model: software key hashes + protected-store semantics.  
QEMU: same functional secure-boot decisions, no hardware-root claim.  
Real platform: OTP/fuse/secure element, protected monotonic state, optional measured boot/TPM, optional CXL IDE evidence.

`HybridBootInfo.SecurityState` remains stable; only evidence strength/capability bits change.

## 11. Acceptance rule

A backend is conformant only if the common behavioral test suite passes unchanged. “Works on QEMU” is not permission to expose QEMU device IDs or ACPI handles in cross-project authority APIs.
