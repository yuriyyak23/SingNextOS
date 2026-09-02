# 05. Full ISE Feature Utilization

## Goal

«Использовать весь функционал ISE» в SingNextOS означает не давать приложениям доступ ко всем 215 code-confirmed opcodes. Это означает, что ОС знает о **семантических классах возможностей** HybridCPU, не препятствует их использованию и предоставляет безопасные capability-gated abstractions там, где операция требует OS policy/resource ownership.

HybridCPU runtime остаётся владельцем decode, legality, typed-slot admission, lane placement, replay and retire. SingNextOS не должен вручную планировать W=8 bundles и не должен превращать physical lane topology в ABI.

## Confirmed ISE topology

Текущий HybridCPU documentation inventory подтверждает 215 code-confirmed instructions из 251 numeric enum values. Physical execution topology:

| Physical carrier | Runtime class | Confirmed role | SingNextOS view |
|---|---|---|---|
| lanes 0–3 | `AluClass` | scalar, vector, MatrixTile compute | transparent compute / typed compute service |
| lanes 4–5 | `LsuClass` | scalar/vector memory | memory-domain governed normal execution |
| lane 6 | `DmaStreamClass` | DSC1 bulk stream compute | `BulkCompute` service candidate |
| lane 6 | `MatrixTileStreamClass` | MatrixTile load/store | Matrix service transport |
| lane 7 | `BranchControl` | branches | transparent execution |
| lane 7 | `SystemSingleton` | system, VMX, event/barrier, accelerator commands | privileged/system services |

Aliased physical lane does **not** merge authority. MatrixTile transport is not DSC. Accelerator control is not branch authority. Public API must preserve semantic separation.

## Layer 1 — transparent ISA utilization

Most instructions should require no explicit public OS API. The external compiler/runtime uses them while executing admitted code.

### Scalar ALU and integer operations

These belong to ordinary execution. SingNextOS responsibility:

- admit executable image/profile;
- bind process to execution domain;
- enforce memory and capability policy around privileged resources;
- never attempt per-opcode capability checks for ordinary arithmetic.

### Vector operations

Vector arithmetic/reduction/permutation should also be primarily transparent. A compute library may expose ergonomic APIs, but the security authority remains process execution/memory domains rather than a `VADD` capability.

Vector compute becomes an explicit OS service only when it crosses ownership/domain/device boundaries or needs reserved compute budgets.

### Scalar/vector LSU

Load/store execution is governed by process address-space and memory-domain policy. `OwnedRegion<T>` affects cross-domain ownership, not every normal stack/local load.

## Layer 2 — explicit compute services

### MatrixTile

Current ISE has:

- `MTILE_LOAD`
- `MTILE_STORE`
- `MTILE_MACC`
- `MTRANSPOSE`

The runtime gives MatrixTile its own architectural tile state, numeric/layout policy, owner checks, execute capture, retire publication, replay and rollback.

Recommended SingNextOS abstraction:

```text
System.Compute.Matrix
  MatrixContext   (capability-backed service/session)
  MatrixTile<T>   (opaque tile resource or logical tile handle)
  LoadAsync
  StoreAsync
  MultiplyAccumulateAsync
  TransposeAsync
```

Public clients pass owned/bounded regions and semantic matrix shape. They do not pass lane6, tile register file addresses or opcodes.

**Status:** ISE code-confirmed; SingNextOS integration candidate; actual external bridge ABI unproven.

### DmaStreamCompute / DSC1

Current confirmed DSC operations:

- Copy
- Add
- Mul
- Fma
- Reduce

DSC1 validates exact ranges, owner/domain, placement and pressure, stages into token-owned buffers, then commits after fresh guards. This is a natural accelerator for large `OwnedRegion<T>` operations.

Recommended abstraction:

```text
System.Compute.Bulk
  CopyAsync
  AddAsync
  MultiplyAsync
  FusedMultiplyAddAsync
  ReduceAsync
```

Authority:

```text
ComputeCapability
+ region ownership/borrow
+ platform compute-domain grant
+ exact range/type policy
+ commit fence
```

**Do not claim:** DSC2 queues, pause/resume, async hardware overlap, coherent DMA/cache or partial completion. Those are not current code-confirmed production contours.

### External accelerators through L7-SDC

Current scoped SystemSingleton commands:

- `ACCEL_QUERY_CAPS`
- `ACCEL_SUBMIT`
- `ACCEL_POLL`
- `ACCEL_WAIT`
- `ACCEL_CANCEL`
- `ACCEL_FENCE`
- `ACCEL_STATUS`

Recommended SingNextOS abstraction:

```text
System.Device.Acceleration
  AcceleratorCapability
  AcceleratorSession
  QueryCapabilities
  Submit
  Poll/Wait
  Cancel
  Fence
  ReadStatus
```

The API must be descriptor-backed and capability-bound, but **must not claim a universal accelerator ABI**. Current L7-SDC contour is scoped and external backend-specific semantics remain platform-owned.

No fallback from a rejected L7 request to DSC, MatrixTile, VectorALU or a software custom accelerator should happen implicitly. Fallback policy, if desired, is a higher-level compute-service decision with explicit semantics.

## Layer 3 — scheduler and synchronization primitives

Lane7 exposes code-confirmed system operations including:

- `YIELD`
- `WFE`
- `SEV`
- `POD_BARRIER`
- `VT_BARRIER`
- `FENCE`
- system/CSR instructions.

SingNextOS should use existing external runtime primitives behind scheduler/event abstractions where a published platform binding exists.

Conceptual bridge:

```text
IPlatformExecutionControl
  YieldCurrent()
  WaitForEvent(...)
  SignalEvent(...)
  PodBarrier(...)
  VirtualThreadBarrier(...)
  PublishMemoryFence(...)
```

Public .NET-like APIs remain `Task`, `ValueTask`, cancellation, wait handles/channels or higher-level synchronization. They do not expose `WFE`/`SEV` as user ABI.

Important: code-confirmed opcode does not prove the existence of a stable external API usable by SingNextOS. Integration remains external-blocked until tested.

## Typed-slot scheduling as a platform advantage

HybridCPU's W=8 typed-slot model can benefit the OS without the OS knowing lane placement.

Potential OS policy inputs:

- process/service execution class;
- CPU budget/priority;
- compute capability quota;
- device/accelerator admission;
- latency versus throughput hint;
- replay-sensitive or deterministic service policy.

The external runtime then performs class admission and deterministic late lane binding.

The OS **must not** pre-assign exact lanes in manifests. Doing so would duplicate a runtime authority that already accounts for live capacity, hazards, replay state and fairness.

## SMT and domain isolation

HybridCPU supports 4-way SMT and explicit domain guard rejection. SingNextOS can exploit this as a hardware/runtime isolation substrate by binding SIP domains to external execution domains/policies.

Possible policy classes:

- ordinary user SIPs may share a core subject to external domain guards;
- driver/device SIPs may receive tighter interference/budget limits;
- kernel/root contour gets non-delegable system authority;
- confidential domains, if ever production-positive, may require dedicated or policy-constrained scheduling.

The OS should request policy intent; external runtime decides live packing legality.

## Replay, deterministic execution and rollback

HybridCPU has explicit replay phase state, deterministic lane reuse and `ReplayToken`-style snapshot/rollback evidence. SingNextOS should consume this primarily as an **evidence and debugging capability**, not as an application authority token.

Potential use cases:

- deterministic SIP failure reproduction;
- driver contract debugging;
- security incident trace correlation;
- performance/replay tuning;
- fault-injection tests;
- future transactional service restart.

Recommended interface:

```text
System.Diagnostics.PlatformEvidence
  CaptureExecutionEvidence(...)
  ExportReplaySummary(...)
  ReadRejectTelemetry(...)
```

Access must require an evidence-read capability and visibility policy. A replay/evidence object cannot be used to mint memory/device/compute rights.

## Telemetry

HybridCPU exports detailed typed-slot, reject, replay, fairness, hazard, lane6/lane7 and SecureCompute-related evidence.

SingNextOS should preserve a strict separation:

- **operational telemetry** for scheduler/tuning;
- **security evidence** for audit/attestation;
- **guest-visible diagnostics** only when explicitly projected;
- **host/platform-only evidence** not visible to ordinary SIPs.

Telemetry may influence a future policy engine — e.g. choosing a compute provider — but must not bypass capability checks.

## Virtualization instruction set

Code-confirmed VMX carriers include `VMXON`, `VMXOFF`, `VMLAUNCH`, `VMRESUME`, `VMREAD`, `VMWRITE`, `VMCLEAR`, `VMPTRLD` on SystemSingleton lane7. This must not be misread as a recommendation to implement the core OS isolation in VMX terms.

HybridCPU's own architecture says VMX is a frozen compatibility frontend and production authority belongs to neutral runtime domains.

Therefore SingNextOS use is:

- **core OS isolation:** neutral platform domain bridge, no VMX dependency;
- **legacy guest compatibility:** optional isolated virtualization service using external compatibility frontend after neutral admission;
- **VMX fields/exits:** projection only, never local kernel state store.

Some VMX-related enum values remain Reserved/None and must never be inferred as executable from naming.

## SecureCompute

SecureCompute is a major desired direction but **not current production-positive ISE execution**. See the dedicated chapter.

Full ISE utilization therefore means the OS architecture reserves a typed `ConfidentialDomain` service boundary, while runtime feature discovery reports it unavailable until HybridCPU exposes a real neutral descriptor registry, owner-bound admission certificate, grant ledger, backend owner and publication chain.

## Assist and stream helpers

StreamEngine, VectorALU, BurstIO, SRF and assists are useful internal runtime mechanisms, but HybridCPU documentation explicitly distinguishes transport/helper state from architectural authority.

SingNextOS should not create public `StreamEngineHandle` or `SRFHandle` simply because the runtime has those objects. If external platform exposes a stable high-level compute service using them internally, the OS consumes that service semantically.

## ISE utilization matrix

| Capability | ISE status | SingNextOS target | Public exposure |
|---|---|---|---|
| scalar ALU/branch | code-confirmed | transparent execution | normal language/.NET |
| vector ALU/reductions | code-confirmed | compiler/runtime + compute libraries | typed vector APIs, no lane |
| LSU | code-confirmed | domain memory | normal memory/Span/owned regions |
| MatrixTile 4-op contour | code-confirmed | matrix compute service | typed Matrix API |
| DSC1 | code-confirmed scoped | bulk owned-region compute | typed BulkCompute API |
| DSC2 / queues / coherent async | future/denied | unavailable | none |
| L7-SDC scoped ACCEL commands | code-confirmed scoped | accelerator service | typed device session |
| YIELD/WFE/SEV/barriers | code-confirmed | scheduler/event bridge | high-level async/sync APIs |
| typed-slot W=8 scheduling | mainline | external execution policy | not public |
| 4-way SMT/domain guards | mainline | domain binding policy | not public |
| replay/telemetry | implemented evidence | diagnostics/evidence service | read-only capability |
| neutral virtualization domains | implemented architecture substrate | platform virtualization bridge | typed VM/domain service |
| VMX compatibility | projection/frozen frontend | legacy guest service only | privileged compatibility API |
| nested virtualization | neutral model, future expansion | child domain service | privileged typed API |
| SecureCompute production backend | not active | fail closed | feature unavailable |
| hardware-rooted proof signing | not current | future external evidence | unavailable until proven |

## Key rule

The best way to exploit HybridCPU is to let HybridCPU remain a specialized CPU/runtime. SingNextOS should express **policy, ownership, capabilities and semantic compute intent**, while HybridCPU expresses **legality, placement, execution staging and hardware publication**.

That is more powerful and safer than attempting to make the OS itself a lane scheduler, VMCS manager or opcode router.