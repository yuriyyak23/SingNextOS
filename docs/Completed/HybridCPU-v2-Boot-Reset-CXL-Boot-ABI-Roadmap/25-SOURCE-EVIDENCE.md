# 25 — Source Evidence and Research Ledger

## 1. Snapshot and method

Research date: **2026-09-18**. Both repositories were inspected from their public GitHub `master` web surfaces. The execution environment could not resolve GitHub for a direct `git clone`, so this roadmap is not presented as a locally cloned SHA-pinned audit. Instead, every architectural conclusion below is tied to a concrete repository path/URL inspected on the research date. Where an existing project roadmap itself states a baseline SHA, that SHA is recorded as the roadmap's own baseline, not asserted as today's repository HEAD.

This limitation matters mainly for reproducibility of exact source lines; it does not change the proposed contracts. Before implementation starts, Phase R0 SHOULD pin both repositories to explicit commit SHAs and archive these same files as the implementation baseline.

## 2. HybridCPU-v2 sources inspected

### H-ROOT — live architecture README

Path: repository root `README.md` / repository landing page  
URL: <https://github.com/yuriyyak23/HybridCPU-v2>

Observed facts used by this roadmap: fixed 8-slot VLIW architecture, 4-way SMT; current machine state decomposes architectural/frontend/scheduler/pipeline/replay/backend/evidence concerns; 32×64-bit architectural x-registers; five-stage cycle execution; 256-byte native bundle; stage/retire semantics are much more defined than reset/firmware/platform boot semantics.

Roadmap use: [01](01-ARCHITECTURAL-CONTEXT.md), [02](02-CURRENT-STATE-ANALYSIS.md), [04](04-RESET-ABI.md).

### H-STATE — `CoreRuntimeState`

Path: `HybridCPU_ISE/CloseToHSL/Core/State/CoreRuntimeState.cs`  
URL: <https://github.com/yuriyyak23/HybridCPU-v2/blob/master/HybridCPU_ISE/CloseToHSL/Core/State/CoreRuntimeState.cs>

Observed facts: central construction/containment of telemetry, assists, scratch/cache/resources, virtual-thread control, binding, frontend/decode/admission/replay/retire/architectural/scheduling/execution/memory-pipeline state. No independent product Reset ABI is established by this constructor surface.

Roadmap consequence: coordinated reset must span state owners and cannot be modeled as an ordinary retired instruction.

### H-RETIRE — architectural register/PC publication

Path: `HybridCPU_ISE/CloseToHSL/Core/Architecture/Registers/Retire/RetireCoordinator.cs`  
URL: <https://github.com/yuriyyak23/HybridCPU-v2/blob/master/HybridCPU_ISE/CloseToHSL/Core/Architecture/Registers/Retire/RetireCoordinator.cs>

Observed facts: normal architectural PC publication writes `CommittedPc`; x0 writeback is ignored. This is steady-state retirement behavior.

Roadmap consequence: reset directly initializes architectural PC/register state out of band, then normal retire ownership resumes. Reset MUST NOT fabricate a retire record.

### H-MEM — timed memory controller

Path: `HybridCPU_ISE/CloseToHSL/Memory/Timing/MemoryCycleController.cs`  
URL: <https://github.com/yuriyyak23/HybridCPU-v2/blob/master/HybridCPU_ISE/CloseToHSL/Memory/Timing/MemoryCycleController.cs>

Observed facts: memory operations already carry runtime `deviceId`, address/size and capture a `PhysicalMemoryBankBinding`; controller/platform cycles and completion state provide a useful backend seam.

Roadmap consequence: the existing runtime device identifier remains an implementation detail. A new platform physical-address map routes ROM/RAM/MMIO/CXL boot aperture without redefining `deviceId` as boot identity.

### H-EXEC — execution WhiteBook

Path: `Documentation/WhiteBook/5. execution-model.md`  
URL: <https://github.com/yuriyyak23/HybridCPU-v2/blob/master/Documentation/WhiteBook/5.%20execution-model.md>

Observed facts: `Processor.CPU_Core`, five-stage IF/ID/EX/MEM/WB model, 256-byte bundle fetch and ordinary pipeline execution are documented.

Roadmap consequence: Stage-0 is normal HybridCPU code fetched from a ROM-mapped physical range; no boot-only execution pipeline is required.

### H-VIRT — virtualization WhiteBook

Path: `Documentation/Virtualization WhiteBook/00_README.md`  
URL: <https://github.com/yuriyyak23/HybridCPU-v2/blob/master/Documentation/Virtualization%20WhiteBook/00_README.md>

Observed architectural theme: neutral runtime owns capability/evidence/memory/I/O/trap semantics; hardware/virtualization materialization is not itself authority.

Roadmap consequence: boot hardware evidence follows the same direction—useful for materialization/correlation, not automatically a SingNext capability.

### H-CXL-RM — HybridCPU virtualization/SecureCompute/CXL roadmap

Path: `docs/virtualization-securecompute-cxl-refactoring-roadmap/README.md`  
URL: <https://github.com/yuriyyak23/HybridCPU-v2/blob/master/docs/virtualization-securecompute-cxl-refactoring-roadmap/README.md>

The document records its own baseline as `7a096d97a77abaa3208c0c371db0d6c3ac37f3f8` (baseline date 2026-09-13). It keeps CXL below authority, requires exact generations/evidence discipline, and does not make a new CXL ISA lane a goal.

Roadmap consequence: this boot proposal extends platform/reset behavior rather than inventing an instruction family.

## 3. SingNextOS sources inspected

### S-CXL-README — CXL refactoring roadmap index/status

Path: `docs/cxl-refactoring-roadmap/README.md`  
URL: <https://github.com/yuriyyak23/SingNextOS/blob/master/docs/cxl-refactoring-roadmap/README.md>

Observed facts: the staged CXL work is largely completed for the software/model scope; real hardware/QEMU phase is explicitly not claimed as executed; later provider/fabric/persistence-related phases build on provider-neutral authority.

Roadmap consequence: boot must consume existing contracts rather than create a second CXL object model.

### S-CXL-P4 — CXL authority and provider decomposition

Path: `docs/cxl-refactoring-roadmap/04-cxl-authority-and-provider-decomposition.md`  
URL: <https://github.com/yuriyyak23/SingNextOS/blob/master/docs/cxl-refactoring-roadmap/04-cxl-authority-and-provider-decomposition.md>

Observed facts: narrow provider roles (`ICxlDiscoveryProvider`, `ICxlIoProvider`, `ICxlMemoryProvider`, `ICxlCoherentAccessProvider`, `ICxlFabricProvider`, security-evidence provider); authority chain remains provider-neutral; provider-private identities include HDM decoder index, HPA↔DPA/interleave, DPA, switch/port routes, MLD/LD binding, component offsets, CCI/mailbox encodings and PCI requester identities; generations invalidate bindings on reset/link/decoder/fabric/rebind changes.

Roadmap consequence: BootInfo may report such values only as typed evidence/diagnostics. It MUST NOT mint region/device authority from them.

### S-CXL-P5 — Type-3 memory provider

Path: `docs/cxl-refactoring-roadmap/05-cxl-type3-memory-provider.md`  
URL: <https://github.com/yuriyyak23/SingNextOS/blob/master/docs/cxl-refactoring-roadmap/05-cxl-type3-memory-provider.md>

Observed facts: a CXL-backed allocation remains ordinary `OwnedRegion`; provider owns discovery, semantic placement→HPA/HDM/fabric binding, provider-private backing, health/reconfiguration/hot-remove/rebind and generation invalidation; region layer owns allocation identity, region authority, MOVE/borrow, `MutationEpoch`, `RegionUse`; raw DPA/decoder/fabric route is not ordinary authority.

Roadmap consequence: firmware boot mapping cannot be handed to applications or reinterpreted as `OwnedRegion`. SingNextOS must admit CXL again after kernel entry.

### S-CXL-P8 — real hardware / QEMU backend roadmap

Path: `docs/cxl-refactoring-roadmap/08-real-hardware-and-qemu-backend.md`  
URL: <https://github.com/yuriyyak23/SingNextOS/blob/master/docs/cxl-refactoring-roadmap/08-real-hardware-and-qemu-backend.md>

Observed facts: desired layering is firmware/PCI enumeration → CXL discovery adapter → capability parser → provider backends → provider-neutral contracts; ACPI/DVSEC/HDM/mailbox layouts stay provider-private; QEMU and physical validation are distinct milestones.

Roadmap consequence: the boot ABI likewise freezes semantics above interchangeable model/QEMU/hardware adapters.

### S-CXL-P9 — fabric manager, pooling and reconfiguration

Path: `docs/cxl-refactoring-roadmap/09-fabric-manager-pooling-and-reconfiguration.md`  
URL: <https://github.com/yuriyyak23/SingNextOS/blob/master/docs/cxl-refactoring-roadmap/09-fabric-manager-pooling-and-reconfiguration.md>

Observed facts: fabric topology/binding has provider-private identities/generations and reconfiguration lifecycle; fabric manager observations are not application authority and must not directly rewrite region authority.

Roadmap consequence: a pre-boot fabric path may be evidence/platform provisioning, but switch/port/route/MLD identity cannot become `BootVolumeId` or `OwnedRegion` authority.

### S-CXL-P12 — cross-project contracts/non-goals

Path: `docs/cxl-refactoring-roadmap/12-cross-project-contracts-and-non-goals.md`  
URL: <https://github.com/yuriyyak23/SingNextOS/blob/master/docs/cxl-refactoring-roadmap/12-cross-project-contracts-and-non-goals.md>

Observed facts: current SingNextOS CXL work intentionally avoids editing HybridCPU/compiler/ISA lane/replay semantics and avoids leaking raw CXL transport identifiers into higher-level authority.

Roadmap consequence: boot additions belong to HybridCPU reset/platform/firmware layers and a narrow cross-project ABI, not a new CXL ISA lane.

### S-POST-CXL — post-CXL operability refactoring roadmap

Path: `docs/post-cxl-operability-refactoring-roadmap/README.md`  
URL: <https://github.com/yuriyyak23/SingNextOS/blob/master/docs/post-cxl-operability-refactoring-roadmap/README.md>

Observed contract direction: capability/ownership authority, provider mappings/device leases and explicit lifecycle remain distinct; exact generation and fail-closed treatment of reset/reconfiguration/stale/ambiguous state are required; restart must not silently inherit stale external authority.

Roadmap consequence: kernel handoff is not an authority-preservation event. Fresh discovery/admission creates new provider/runtime generations even when firmware evidence points to the same physical device.

## 4. External CXL/PCI/QEMU references

### E-QEMU-CXL — QEMU CXL device model

URL: <https://www.qemu.org/docs/master/system/devices/cxl.html>

Observed capabilities relevant to validation: CXL host bridge/root-port/switch topologies; Type-3 devices with volatile and/or persistent backing; file-backed memory; separate LSA backing; serial-number property; host fixed memory windows; endpoint/host HDM decoder concepts; multi-device/interleave examples.

Roadmap use: QEMU can validate protocol/topology/media assumptions independently of HybridCPU instruction execution. See [18](18-QEMU-AND-REAL-HARDWARE-PATH.md).

### E-LINUX-PCI-DSN — PCI Device Serial Number support

URL: <https://docs.kernel.org/7.0/driver-api/pci/pci.html>

Observed fact used narrowly: Linux exposes PCI DSN support (`PCI_EXT_CAP_ID_DSN` / `pci_get_dsn`) and treats DSN as an optional PCIe extended capability, not a universal required identifier.

Roadmap consequence: DSN is useful optional physical evidence/pinning, not the logical SingNextOS boot identity.

### E-LINUX-CXL-LSA — CXL LSA via mailbox

URL: <https://android.googlesource.com/kernel/common/%2B/9e5737bd0457955690d871b3f4fc66dea40ea141/drivers/cxl/pmem.c>

Observed fact used narrowly: CXL PMEM code accesses LSA through a CXL mailbox `GET_LSA` command with offset/length and an advertised LSA size. This supports an architecture where LSA can be management-path metadata distinct from CXL.mem persistent-capacity mapping when the mailbox is reachable.

Roadmap consequence: LSA MAY carry a small boot locator before full data mapping, but because mailbox/LSA availability is platform-dependent, it is not mandatory/canonical.

## 5. Traceability: findings → architecture decisions

| Finding | Source(s) | Resulting decision |
|---|---|---|
| Existing HybridCPU execution is well-defined after a PC exists; reset/ROM boot is not frozen in inspected surfaces | H-ROOT, H-STATE, H-EXEC, H-RETIRE | BR-001/002: explicit local ROM/reset ABI; reset initializes state out of band |
| Existing memory controller has implementation `deviceId`/binding | H-MEM | BR-003: never promote runtime device index to boot identity |
| CXL provider keeps DPA/HDM/fabric IDs private | S-CXL-P4, S-CXL-P5 | BR-013/014: CXL evidence only in BootInfo; fresh OS admission |
| Type-3 backing remains ordinary `OwnedRegion` authority | S-CXL-P5 | No `CxlBootRegion`/firmware-owned OS capability |
| Fabric manager does not own region authority | S-CXL-P9 | Route/port/MLD cannot be semantic boot identity/authority |
| Restart/reconfiguration requires fresh generations/fail-closed stale state | S-POST-CXL | Warm reset may retain hardware state but never software authority (BR-018) |
| Existing cross-project plans avoid CXL ISA lanes | H-CXL-RM, S-CXL-P12 | BR-015: no CXL-specific ISA change |
| QEMU models Type-3 persistent memory, LSA and HDM topology | E-QEMU-CXL, S-CXL-P8 | BR-017 and R11: protocol/model validation separated from CPU execution |
| PCI DSN is optional extended-capability data | E-LINUX-PCI-DSN | BR-005: optional physical evidence only |
| LSA is mailbox-addressable management data when supported | E-LINUX-CXL-LSA | BR-006: optional locator; canonical signed metadata stays in persistent capacity |

## 6. Claims deliberately not made

This roadmap does **not** claim that today's HybridCPU repository has no interrupt, MMIO, privilege or loader code anywhere. It states only that the inspected architecture/source surfaces do not freeze the product reset vector, immutable ROM, global platform map and boot handoff contracts needed here. It also does not claim that every physical CXL device exposes LSA/DSN in a boot-usable way, that QEMU behavior proves silicon behavior, or that a particular real host permits firmware to reprogram every HDM decoder.

Those platform-specific questions are intentionally isolated in [23-OPEN-QUESTIONS.md](23-OPEN-QUESTIONS.md) so they can be resolved without reopening semantic identity or authority decisions.

## 7. Implementation-baseline action

Before the first code change, record:

```text
HybridCPU-v2 implementation SHA = UNSET_AT_RESEARCH_SNAPSHOT  # record at Phase R0
SingNextOS implementation SHA   = UNSET_AT_RESEARCH_SNAPSHOT  # record at Phase R0
QEMU validation version         = UNSET_AT_RESEARCH_SNAPSHOT  # record at Phase R11
CXL target revision/profile     = UNSET_AT_RESEARCH_SNAPSHOT  # record before Phase R12
```

Then archive the exact versions of H-STATE/H-RETIRE/H-MEM/H-CXL-RM and S-CXL-P4/P5/P8/P9/P12/S-POST-CXL in CI documentation. If those sources changed materially since this 2026-09-18 research snapshot, reconcile differences through a new ADR before implementing the affected phase.
