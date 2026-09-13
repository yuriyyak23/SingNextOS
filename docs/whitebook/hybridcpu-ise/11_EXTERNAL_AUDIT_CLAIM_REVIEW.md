# 11. External Audit Claim Review

## Purpose

Внешний архитектурный аудит предложил сильную интерпретацию SingNextOS + HybridCPU-v2 как zero-copy, near-zero-overhead, deterministic dataflow OS. Многие идеи полезны как направление, но часть формулировок смешивает:

- **code-confirmed ISE behavior**;
- **plausible SingNextOS integration**;
- **future research direction**;
- **unsupported performance/security overclaim**.

Этот раздел сохраняет ценные идеи и одновременно фиксирует границы доказанного поведения.

## Review vocabulary

- **Confirmed** — прямо поддержано текущими HybridCPU-v2 implementation/docs.
- **Strong integration candidate** — логично следует из текущих SingNextOS primitives + ISE contour, но внешний ABI ещё не доказан.
- **Needs reframing** — идея полезна, но исходная формулировка технически неточна.
- **Unsupported / reject as current claim** — текущий HybridCPU не доказывает это свойство, поэтому WhiteBook не может объявлять его фактом.

## 1. “Hardware Zero-Copy through DmaStreamCompute lane 6”

### External claim

Lane6 `DmaStreamComputeMicroOp` supposedly lets SingNextOS move arbitrary arrays between processes/devices without CPU touching the data bus; OS verifies capabilities and directly forms a VLIW bundle for DMA.

### Verdict: **Needs reframing**

Current HybridCPU confirms a scoped DSC1 contour on lane6 with exact ranges, owner/domain/placement validation, token-owned staging, all-or-none commit and rollback on partial physical write failure. That is highly valuable for bulk data movement/compute.

But three distinctions are essential.

### A. Offloaded copy is not zero-copy

`Copy` through DSC still moves bytes from source to destination. It avoids a scalar `memcpy` loop, but it is still a physical copy.

Strict zero-copy IPC in SingNextOS means:

```text
same backing region
+ ownership transfer or temporary borrow
+ mapping/domain rebinding
= no payload bytes copied
```

Therefore WhiteBook uses three separate terms:

- **ownership zero-copy** — bytes stay in the same backing region; authority changes;
- **direct I/O** — device accesses an already-owned mapped region directly;
- **offloaded bulk copy/transform** — DSC moves/transforms bytes without scalar CPU copy loops.

These mechanisms complement each other and must not be conflated.

### B. SingNextOS must not form physical VLIW bundles as an authority step

HybridCPU compiler/runtime owns canonical carrier generation, decode, typed-slot legality and lane materialization. SingNextOS should issue a semantic `BulkCompute` request through a platform bridge, not construct lane6 bundles itself.

Correct direction:

```text
OwnedRegion + ComputeCapability
 -> SingNextOS kernel validation
 -> platform BulkCompute request
 -> external HybridCPU runtime chooses/legalizes carrier
 -> DSC staged execution/commit
```

### C. “CPU does not touch the bus” is not established

Current WhiteBook describes a runtime helper that reads/writes exact physical ranges through the DSC backend and explicitly notes that current helper work is synchronous and that coherent DMA/cache and async overlap are not current features. It does not prove a silicon-level independent DMA engine that moves “gigabytes” with zero CPU/memory-system involvement.

### Adopted idea

Use DSC1 as a first-class **bulk owned-region compute provider**, not as the definition of zero-copy itself.

---

## 2. “MatrixTile zero-copy / direct RAM-to-MatrixRegisterFile”

### External claim

`MTILE_LOAD` supposedly loads directly from RAM into architectural `MatrixRegisterFile`, bypassing scalar registers/general caches, after which tile ownership can move between processes or L7 accelerators by transferring a memory token.

### Verdict: **Needs substantial reframing**

HybridCPU does confirm a dedicated MatrixTile execution plane and architectural `MatrixTileArchitecturalTileRegisterFile`. It also confirms typed lane6 MatrixTile stream transport.

However the live data path is specifically:

```text
MainMemory rows
 -> typed MatrixTile ingress
 -> lane6 SRF row windows
 -> staged execute capture
 -> retire validation
 -> architectural MatrixTile register file
```

The HybridCPU documentation explicitly says it is inaccurate to describe `MTILE_LOAD` as a generic DMA path that loads directly into the architectural tile register file.

Further, an architectural tile image is owner/thread/tile-id state. A memory ownership token does not transfer that architectural state to another SIP or accelerator.

Correct inter-domain transfer is:

```text
architectural tile
 -> MTILE_STORE into an owned memory region
 -> retire all-or-none memory publication
 -> transfer/borrow the OwnedRegion
 -> receiving compute domain loads its own tile state
```

### Adopted idea

MatrixTile should become a typed OS compute service with owned-region ingress/egress, but tile register state remains platform/runtime-owned and is never a transferable SingNextOS memory capability.

---

## 3. “Context switch disappears / suspend in one cycle”

### External claim

4-way SMT, shared physical registers and per-VT rename maps allegedly make context switching essentially free; SingNextOS can suspend a virtual thread in one cycle by changing a `VirtualThreadId` and Domain Guard state.

### Verdict: **Unsupported as a current claim**

HybridCPU does retain explicit:

- `PhysicalRegisterFile`;
- per-VT `RenameMap`;
- `CommitMap`;
- `FreeList`;
- 4-way SMT typed-slot scheduling.

This can plausibly reduce some scheduling/context overhead relative to a design that always spills every logical thread context to memory.

But current docs do **not** establish:

- one-cycle OS context switch;
- zero register save/restore for arbitrary task migration;
- zero TLB/address-space switching cost;
- zero pipeline drain/serialization cost;
- arbitrary number of SingNextOS tasks resident in four hardware VT contexts;
- architectural vector/matrix state being “just memory.”

In fact MatrixTile has a distinct architectural register file, and SRF is explicitly transient staging, not architectural thread state.

### Adopted idea

SingNextOS scheduler should exploit **resident hardware virtual-thread contexts and event-driven parking** if the external platform exposes them, but exact suspend/resume latency must remain a measured platform property, not an architectural promise.

Recommended wording:

> HybridCPU SMT may enable low-overhead resident-thread switching and reduce spill/reload pressure for a bounded number of live hardware VTs; SingNextOS should preserve this opportunity through a platform scheduling bridge without promising a fixed one-cycle context switch.

---

## 4. “Capability token is checked in hardware against IOMMU at every DSC/L7 operation”

### External claim

SingNextOS issues a capability token; `IRuntimeLegalityService` compares it against IOMMU mappings; revocation causes a hardware fence to instantly block memory-bus access.

### Verdict: **Strong architectural direction, unsupported implementation claim**

The philosophy is correct: operation-time domain/owner/range checks plus revocation must guard DMA/accelerator access.

But current HybridCPU artifacts do not expose SingNextOS `CapabilityDescriptorV1` as an ISE token, and SecureCompute docs explicitly note missing production grant-ledger/effect-path enforcement for several secure memory/I/O policies.

Also:

```text
SingNextOS CapabilityId != HybridCPU native token
```

The two must remain separate authorities.

Correct future model:

```text
local SingNextOS capability
+ local RegionGeneration
+ external memory/I/O domain lease
+ external operation-specific grant
= platform may stage operation
```

Revocation must close new work and cancel/drain/invalidate platform mappings/grants according to the actual external contract. “Instant hardware fence blocks the bus” must not be promised until a concrete IOMMU/DMA revocation interface is proven.

### Adopted idea

Capability-gated direct memory access is a core design objective, implemented as **dual authority** rather than one shared token format.

---

## 5. “Deterministic I/O means hard real-time and exactly N cycles”

### External claim

Typed-slot deterministic scheduling + Whole-Program Admission allegedly lets SingNextOS mathematically prove exact cycle counts and remove jitter because StreamEngine/assists eliminate cache misses.

### Verdict: **Reject as current claim**

HybridCPU does provide deterministic late lane binding, explicit legality/reject taxonomy and replay-bounded determinism evidence. This is valuable for analyzability.

But the current architecture explicitly includes dynamic runtime factors:

- live class capacity;
- scoreboard pressure;
- bank-pending rejection;
- hardware memory budget;
- speculation budget;
- fairness;
- replay state;
- cache/SRF residency;
- memory hazards.

HybridCPU documentation describes replay determinism as an **evidence-bounded envelope**, not a global determinism theorem. Current fence semantics are not a cache/TLB/DMA/global-coherence theorem.

Nothing in the audited docs proves:

- absence of cache misses;
- fixed memory latency;
- exact cycle-count WCET for arbitrary programs;
- hard real-time scheduling certification;
- zero jitter for external devices/accelerators.

SingNextOS `AdmissionVerifier` currently proves a bounded set of managed-code/profile restrictions and dependency rules, not WCET.

### Adopted idea

HybridCPU is promising for **analyzable real-time profiles**, because it exposes typed resource classes, explicit rejects, deterministic lane choice and rich evidence. A future `RealTimeExecutionProfile` could be researched, but it requires:

- bounded memory hierarchy contract;
- timer/interrupt bound;
- external device latency model;
- admission schedulability analysis;
- WCET proof/evidence;
- interference limits across SMT/domains;
- release conformance tests.

Until then use “deterministic/replay-aware scheduling” rather than “hard real-time OS.”

---

## 6. “Assist Plane can perform GC, security scans and OS background services”

### External claim

Architecturally invisible assists could scan GC heaps, search memory for malware/key leakage, gather telemetry and warm data without interrupting main code.

### Verdict: **Warming confirmed; GC/security sweeps unsupported**

HybridCPU confirms a narrow assist plane:

- architecturally invisible;
- non-retiring;
- replay-discardable;
- bounded warming only;
- cache prefetch or SRF prefetch;
- owner/context/domain/replay/quota/backpressure checks.

Assists explicitly do not:

- write architectural registers;
- publish architectural faults;
- commit memory;
- execute VectorALU;
- accept DSC or MTILE descriptors;
- become owner/replay authority.

Therefore a GC mark/sweep engine or antivirus/key-scanning engine is **not** a current assist feature.

Background GC would require reading object graph semantics, updating GC metadata and coordinating with mutators — well beyond bounded prefetch. Security sweeps similarly require a defined memory/evidence authority and could violate confidential-domain non-leak policy.

### Adopted idea

Use assists for what they actually are good at:

- prefetch/warming before predictable compute or I/O;
- reducing cold-start latency for bounded streams;
- runtime-controlled residency optimization;
- telemetry about assist effectiveness.

Research-only future ideas such as GC prefetch hints may be explored, but assists must remain non-authoritative and non-committing.

---

## 7. “L7-SDC creates unified memory with guarded token commit”

### External claim

Accelerators access the same physical memory as CPU; `Fence` + `RetireCoordinator` make results coherently visible, eliminating CPU/device synchronization problems.

### Verdict: **Staged command/commit confirmed; universal unified memory rejected**

HybridCPU confirms scoped L7-SDC command carriers, guarded tokens, backend staging, register ABI, fence helpers and an explicit commit coordinator.

However current memory-conflict documentation explicitly says the accelerator conflict manager is model-local and **not** a global CPU load/store ordering authority. There is no mandatory global hook joining CPU load/store/atomic, DSC, DMA, StreamEngine/SRF/assist, L7, cache and cancellation. Such a `GlobalMemoryConflictService` is future work.

Therefore current L7-SDC does not prove:

- universal coherent shared memory;
- CPU/device cache coherence;
- automatic ordering with all CPU loads/stores;
- zero-copy direct access to every SingNextOS owned region;
- absence of explicit invalidation/mapping steps.

### Adopted idea

Use L7-SDC as a typed **accelerator command service** with staged results and explicit memory-footprint bindings. When the external platform eventually provides global memory conflict/coherence authority, SingNextOS can widen direct owned-region mappings without changing public capability semantics.

---

## 8. “Zero-copy is the only way to move data”

### Verdict: **Reject**

HybridCPU has ordinary scalar/vector load/store instructions and DSC `Copy`. SingNextOS may need copying for:

- security boundary sanitization;
- immutable snapshot creation;
- different representation/layout;
- ManagedGc to owned-region transition;
- non-shareable device memory;
- unavailable platform domain rebinding;
- compatibility protocols.

The correct principle is:

> **Ownership transfer is the preferred cross-SIP large-payload primitive; copying is explicit and policy-driven, not forbidden.**

This matches the existing SingNextOS architecture better than “zero-copy only.”

---

## 9. “Memory becomes streams and MatrixTiles instead of pages/bytes”

### Verdict: **Useful programming model, wrong security substrate if literal**

Streams and tiles are valuable compute abstractions. But HybridCPU neutral memory domains still own address spaces, translation, nested page walking, dirty tracking, DMA windows and IOMMU policy.

SingNextOS also needs ordinary byte-addressable regions and page/address-space mechanisms under the hood.

Correct layered view:

```text
Security/storage substrate:
  address spaces + regions + ownership + mappings + IOMMU

Compute views:
  spans + streams + vectors + matrix tiles + accelerator descriptors
```

Compute views never replace memory ownership/translation authority.

---

## 10. “Isolation is not page tables, but cryptographic/structural tokens and Guard Planes”

### Verdict: **Needs reframing**

Guard planes, capabilities and generations are important, but HybridCPU virtualization explicitly includes neutral address spaces, translation, nested page walking and IOMMU domains. SecureCompute also states that it is not CHERI/tagged-memory/capability-aware LOAD/STORE.

Therefore isolation is **compositional**:

```text
address-space / translation isolation
+ domain/owner guards
+ local SingNextOS capabilities
+ ownership generations
+ external typed grants
+ publication fences
```

No single token mechanism replaces all memory translation/protection.

---

## 11. “OS as verifier instead of dispatcher”

### Verdict: **Adopt, with one correction**

This is the strongest idea in the external audit.

SingNextOS should increasingly act as:

- contract verifier;
- capability issuer/revoker;
- ownership authority;
- platform-domain orchestrator;
- policy compiler/admission gate;
- evidence consumer;
- service composition layer.

But it remains a scheduler/resource manager too. HybridCPU runtime owns fine-grained typed-slot legality and placement; SingNextOS owns higher-level process/domain priorities, budgets, lifecycle, capability delegation and service policy.

Better formulation:

> **SingNextOS is a policy/verifier OS that delegates fine-grained execution placement to HybridCPU's neutral runtime while retaining privileged ownership of OS principals and capabilities.**

## Final synthesis

The external audit is directionally valuable when interpreted as a **research agenda**, not a current feature list.

### Keep

- ownership-first large-payload IPC;
- direct device access to capability-bound owned regions where external IOMMU policy allows;
- DSC bulk offload;
- MatrixTile service;
- L7 accelerator command service;
- low-overhead hardware-VT-aware scheduling as a target;
- assist-driven warming;
- evidence-rich deterministic runtime;
- “OS as verifier/orchestrator” philosophy.

### Reframe

- zero-copy versus offloaded copy;
- matrix tile state versus transferable memory;
- local capability versus platform grant;
- deterministic scheduling versus hard real-time;
- resident SMT switching versus one-cycle context switch;
- staged L7 commit versus global coherent unified memory.

### Reject as present-tense claims

- exact N-cycle arbitrary I/O;
- no cache misses/jitter;
- one-cycle arbitrary context switching;
- current GC/security sweep assists;
- universal CPU/device coherent unified memory;
- hardware enforcement of SingNextOS CapabilityId tokens by ISE;
- DSC as a proven independent silicon DMA engine with guaranteed CPU-free bus transfer;
- SecureCompute as production-active confidential execution.

This distinction is essential for a “бескомпромиссная ОС”: strong claims must be stronger because they are verifiable, not because they are absolute.