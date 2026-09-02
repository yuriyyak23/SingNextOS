# 08. Delta From The Previous Singularity+ WhiteBook

## Source vision

The previous 7-page Singularity+ WhiteBook proposed a broad operating-system vision built around:

- Rust-like ownership/lifetimes in Sing#/C#;
- Hypervisor-as-Firmware replacing UEFI/BIOS;
- all services as SIPs;
- capability-only MMIO/DMA/IRQ;
- typed IPC with zero-copy ownership transfer;
- Bartok-RS borrow checking and new IR layers;
- UHDL “driver as data” manifests;
- automatic driver/HDL/testbench generation;
- unified heterogeneous compute abstraction;
- unified CPU/GPU/FPGA virtual memory;
- .NET-like system APIs;
- SIP tasks/goroutine-style concurrency;
- deterministic tracing, snapshots and hot updates.

The current SingNextOS implementation and the audited HybridCPU-v2 architecture let us separate the durable ideas from assumptions that should be retired or deferred.

## Keep as core architecture

### Ownership and lifetimes

**Previous vision:** owned resources, move semantics, borrow checking, deterministic release.

**Current direction:** keep and strengthen.

SingNextOS already has generation-bound owned regions/buffers, runtime transfer, borrow leases and analyzer enforcement. This is now a real architectural foundation rather than a hypothetical language extension.

Recommended evolution:

- continue C#/.NET ergonomic APIs;
- preserve `OwnedRegion<T>`, `OwnedBuffer<T>`, `BorrowLease<T>` semantics;
- improve analyzer/generator precision;
- avoid depending on compiler/backend modifications for safety correctness.

### Capability-only privileged resource access

**Previous vision:** MMIO/DMA/IRQ only via capabilities.

**Current direction:** keep exactly, but split local OS capability from external HybridCPU grant.

This is one of the strongest aligned ideas.

### SIP contracts and zero-copy ownership transfer

**Previous vision:** typed IPC contracts with linear ownership and zero-copy transfer.

**Current direction:** keep as the primary large-payload IPC model.

Important refinement:

- zero-copy means transfer/rebinding of one backing region, not DSC `Copy`;
- borrowed access is generation-bound and revocable;
- shared mutable memory remains explicit and exceptional.

### .NET-like system API

**Previous vision:** idiomatic `System.*` namespaces and owned handles/resources.

**Current direction:** keep.

The implementation should expose normal async/await/ValueTask ergonomics and typed resource objects while keeping hardware/platform authority behind SIP/kernel bridges.

### Deterministic artifacts and testability

**Previous vision:** deterministic trace, formal contracts, generated state machines.

**Current direction:** keep the spirit, ground it in actual mechanisms:

- deterministic protocol/manifests;
- admission proofs;
- generated typed SIP metadata;
- fail-closed runtime validation;
- HybridCPU replay/legality/telemetry evidence where externally exposed.

Do not turn determinism into an unsupported claim of exact-cycle hard real-time.

## Reframe

### Hypervisor-as-Firmware

**Previous vision:** a native hypervisor starts from reset, owns MMU/IOMMU/DMA, device discovery, power, snapshots and root SIP startup.

**New framing:** **neutral Platform Authority Layer**, not VMX-centric Hypervisor-as-Firmware.

Reasons:

- HybridCPU virtualization architecture explicitly says neutral runtime owners hold execution/memory/I/O/capability/evidence authority;
- VMX is a frozen compatibility frontend;
- SingNextOS must not introduce its own mutable VMCS manager or duplicate external runtime state.

What survives from the old idea:

- small trusted machine/platform boundary;
- measured/verified boot direction;
- central resource-domain binding;
- compatibility firmware as an isolated service where needed.

What changes:

- SingNextOS consumes existing HybridCPU platform authority through a typed bridge;
- it does not replace or redesign HybridCPU firmware/runtime in this repository.

### “All domains are SIPs”

**Previous vision:** every isolated domain is a SIP.

**New framing:** SIP is the OS principal/service model; HybridCPU execution/memory/I/O/nested/secure domains are separate external authority dimensions.

One SIP domain can have multiple platform-domain bindings. A platform child domain need not be represented as a user-visible SIP unless OS policy chooses to expose it that way.

### Unified heterogeneous compute

**Previous vision:** one Compute IR automatically targets CPU/GPU/FPGA/network.

**New framing:** semantic compute services with provider discovery.

Near-term provider families:

- normal CPU/vector execution;
- MatrixTile;
- DSC1 bulk compute;
- scoped L7 accelerators.

Public API may be unified at a higher semantic level, but SingNextOS does not implement a new compiler IR/backend stack in this phase.

### Unified virtual memory

**Previous vision:** one CPU/GPU/FPGA virtual address space with ownership grants.

**New framing:** owned regions + explicit memory/I/O domain binding + direct mapping when the external platform proves it.

Universal coherent unified memory is not assumed. Copies remain explicit fallback where sharing/rebinding is unavailable or unsafe.

### Driver-as-data / UHDL

**Previous vision:** devices publish a rich manifest that generates drivers, HDL and testbenches.

**New framing:** keep **manifest-driven capability declaration** and generated typed stubs/state machines; defer universal hardware-description/HDL generation.

Current practical path:

- `DriverManifestV1` describes identity and required capabilities;
- generated SIP contracts describe driver service ABI;
- external platform binding supplies actual MMIO/IRQ/DMA/device semantics;
- richer device metadata can be added only when a real device family/use case justifies it.

A universal UHDL is not required to benefit from HybridCPU L7/DSC/Matrix services.

### Firmware compatibility service

**Previous vision:** optional UEFI-compatible SIP.

**New framing:** conceptually valid but future scope.

Legacy firmware/VMX compatibility should be isolated downstream of neutral platform authority. It must not become the root state model.

## Defer

### Bartok-RS compiler fork and new owned C# language

Current SingNextOS intentionally treats compiler/backend/runtime as black boxes. The project already obtains useful safety from analyzers, source generators, explicit runtime types and admission verification.

Defer language/compiler modifications until a concrete safety gap cannot be closed locally.

### HIIR / ASIR / DOIR compiler pipeline

This would duplicate or compete with the existing HybridCPU compiler/runtime contract and typed-slot machinery.

Defer. Use semantic SIP/platform contracts instead.

### PTX/SPIR-V/HDL multi-backends

Not needed for the first-class HybridCPU compute providers available in the ISE. Provider-specific external adapters can be added later without changing kernel authority semantics.

### Manifest-to-Silicon HDL generation

Interesting research direction, but orthogonal to bringing up a secure OS on HybridCPU.

Defer until:

- driver/service contracts stabilize;
- real devices expose stable machine-readable semantics;
- a separate toolchain project can own synthesis/verification.

### Full driver factory

The current priority is not automatically generating all drivers. It is establishing correct MMIO/IRQ/DMA/device capabilities and domain-backed zero-copy. Driver generation can build on that later.

### Global transactional snapshots and hot hypervisor updates

Defer until ordinary domain lifecycle, external platform bindings, DMA drain/revocation and checkpoint classification exist. Secure migration especially remains future-gated by HybridCPU.

### Distributed primitive migration across network/FPGA

This is a higher-level orchestration feature. It should not drive the trusted kernel architecture before local compute/domain semantics are proven.

## Remove as architectural assumptions

### “VMX/hypervisor owns every system fact”

Remove. Neutral runtime owners must be primary; VMX compatibility is downstream.

### “Compiler metadata is security authority”

Remove. Compiler/generator metadata can prove shape and intent but live runtime admission remains authoritative.

### “A handle/token value can be directly trusted by hardware and OS”

Remove. Local capability IDs, region handles, platform tokens and evidence objects have distinct trust domains.

### “Zero-copy eliminates copying everywhere”

Remove. Ownership transfer is preferred for large cross-SIP payloads, but explicit copying remains valid and sometimes necessary.

### “Unified memory/coherence is already guaranteed”

Remove. Current HybridCPU L7/DSC/stream conflict/coherence contours do not prove universal global coherence.

### “Deterministic scheduling implies exact-cycle hard real-time”

Remove. Deterministic lane choice and replay evidence improve analyzability but do not by themselves establish WCET or fixed device/memory latency.

## Old roadmap versus new dependency order

Previous roadmap:

```text
hypervisor -> SIP kernel -> driver factory -> UHDL -> production compute stack
```

Recommended SingNextOS roadmap:

```text
1. typed SIP + ownership + capability foundation
2. protocol request/response shape and publication semantics
3. local domain/platform binding abstractions
4. HybridCPU execution/memory/I/O bridge qualification
5. owned-region DMA/direct mapping
6. MatrixTile / DSC1 / L7 scoped compute services
7. neutral virtualization/nested domain service
8. evidence/replay/telemetry projection
9. SecureCompute only when production-positive externally
10. higher system services and richer device/compute orchestration
```

## API continuity with the old vision

The user-facing style can remain remarkably close to the old WhiteBook even though the internal architecture changes.

Examples:

```text
System.Compute.Matrix.OpenAsync(...)
System.Compute.Bulk.TransformAsync(...)
System.Device.Accelerator.OpenAsync(...)
System.Virtualization.Domain.CreateAsync(...)
System.Security.Confidential.OpenAsync(...)
```

All can use ordinary .NET async ergonomics while passing owned regions and typed capabilities internally.

The difference is that none of these APIs assumes a particular physical lane, VMX opcode, raw handle or compiler backend.

## Final disposition table

| Previous idea | Decision | New interpretation |
|---|---|---|
| ownership/lifetimes | Keep | core SingNextOS IPC/memory primitive |
| capability MMIO/DMA/IRQ | Keep | dual local + platform authority |
| SIP typed contracts | Keep | generated protocol + runtime validation |
| zero-copy ownership | Keep/refine | transfer/rebind, not bulk copy |
| .NET-like API | Keep | ergonomic façade over SIP/kernel/platform bridge |
| Hypervisor-as-Firmware | Reframe | neutral Platform Authority Layer |
| all domains = SIP | Reframe | OS principal vs multiple platform domains |
| unified compute IR | Reframe/defer | semantic compute services/providers |
| unified virtual memory | Reframe | owned-region direct mapping where proven |
| UHDL driver factory | Narrow/defer | manifests + typed service generation first |
| Bartok-RS/compiler fork | Defer | analyzers/generators/runtime types first |
| HDL generation | Defer | separate future tooling |
| UEFI/legacy service | Defer | isolated compatibility SIP |
| global snapshots | Defer | require domain/drain/restore contracts |
| hard real-time by determinism | Remove as current claim | future explicit RT profile/research |
| universal coherent accelerators | Remove as current claim | scoped L7 + explicit conflict/coherence gates |

## Decision

The previous Singularity+ WhiteBook remains a valuable vision document. The current audit converts it from a broad “new stack replaces everything” proposal into a **grounded HybridCPU-native OS architecture**: preserve ownership, capabilities, SIPs and .NET ergonomics; use ISE-specific strengths through a neutral platform bridge; refuse to make compiler, VMX, telemetry, tokens or physical lanes into authority.