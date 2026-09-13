# SingNextOS

**Capability-native domain-host operating-system research platform for HybridCPU-v2, typed service isolation, ownership-oriented IPC, neutral platform authority, and heterogeneous memory/device execution.**

SingNextOS explores an operating-system architecture in which **OS authority, ownership, publication, and reclamation remain owned by SingNextOS**, while execution, memory, I/O, accelerator, virtualization, and fabric effects are expressed through narrow semantic platform contracts. The system is implemented in C#/.NET and is designed around generated typed SIP contracts rather than a large untyped syscall ABI.

> **Project status:** research build and architecture/conformance platform. Provider capabilities are advertised explicitly as `ModelOnly`, `ProjectionOnly`, `RuntimeAdmission`, `Executable`, or `ProductionSecure`; weaker states are never implicitly promoted to stronger ones. Source and tests are authoritative when documentation becomes stale.

## Architectural goals

SingNextOS is built around several constraints that are intentionally stronger than a conventional handle/syscall model:

- **Authority stays local to the OS.** HybridCPU or another platform provider may admit and execute an operation, but it does not mint SingNextOS capabilities or decide local ownership.
- **Identity spaces do not alias.** A `DomainId`, `ProcessHandle`, `RegionHandle`, `EndpointSessionHandle`, provider lease, child-domain lease, mapping ID, or external operation ID is a distinct identity with its own generation/lifetime rules.
- **Discovery is not authority.** Discovering a service or a platform feature does not grant permission to use it.
- **Ownership precedes sharing.** Mutable memory has an explicit owner; sharing is represented as bounded borrow/use/grant state rather than ambient shared mutable memory.
- **Admission is not execution, completion is not visibility, and visibility is not publication.** These states are represented separately.
- **Unknown external state fails closed.** Ambiguous accepted work is quarantined/pinned until exact closure or independently proven effect containment.
- **Platform contracts are semantic.** Application and SIP APIs do not carry physical addresses, page-table roots, IOMMU identifiers, HybridCPU lanes, raw opcodes, VMCS fields, CXL HDM/DPA topology, or provider tokens.
- **NeutralRuntime is a control plane.** Payload data remains in owned regions/buffers and provider mappings; the neutral boundary describes authority and lifecycle rather than becoming the data hot path.
- **Compatibility remains downstream.** POSIX, Win32, Wine, VMX/VMCS, and other compatibility personalities may project native state, but they do not define native SingNextOS authority or IPC semantics.

## Architecture at a glance

```text
Application / source-facing Sing+ libraries
                |
                v
Generated typed SIP client + protocol metadata
                |
                v
Service discovery -> EndpointSession
                |
                v
Capabilities + ownership + protocol state
                |
                v
RuntimeKernel
  |-- CapabilityAuthority
  |-- RegionAuthority
  |-- service/session authority
  |-- component/process/domain lifecycle
  |-- device/driver/virtualization authority
  |-- publication/reclaim decisions
                |
                v
PlatformAuthorityBridge
                |
                v
Provider-neutral contracts / NeutralRuntime
                |
        +-------+------------------+
        |                          |
        v                          v
Host / model providers       explicit executable adapters
                                   |
                                   v
                           HybridCPU ExternalRuntime

Additional provider families such as CXL plug into the same authority,
region-use, external-operation, completion, publication, and reclaim model.
```

The central co-design rule is:

```text
local Sing authority
AND exact live local identity/generation/session state
AND required ownership state
AND live provider/platform authority
AND truthful feature availability
    -> an external effect may be admitted
```

No term substitutes for another.

## Intent, authority, evidence, publication

SingNextOS treats four classes of objects differently:

| Class | Meaning | Examples |
|---|---|---|
| **Intent** | What a caller wants to do | SIP request, compute request, mapping request, child-domain request |
| **Authority** | Live permission to perform an effect | capability, region ownership, borrow lease, device lease, platform binding |
| **Evidence** | Observation or proof about state | completion receipt, measurement, replay evidence, platform feature descriptor |
| **Publication** | Commit point at which a result becomes architecturally visible | response publication, ownership return, staged output commit, lifecycle state publication |

An evidence object never becomes authority merely because it is well-formed or cryptographically identified. Likewise, a backend success code does not by itself authorize publication or local reclamation.

## Capability model

The core capability descriptor is `CapabilityDescriptorV1`:

```text
CapabilityId
IssuerDomainId
SubjectDomainId
ResourceKind
ResourceId
Rights
Generation
RevocationEpoch
```

Current semantic rights include `Read`, `Write`, `Map`, `Signal`, `Configure`, `Transfer`, `Delegate`, and `Execute`. Resource classes cover kernel services, memory, channels, devices, MMIO, IRQ, DMA, compute, virtualization, evidence, SecureCompute, process control, filesystem/network objects, and native UI roles.

Capabilities are explicitly subject-bound and generation-bound. Delegation must derive a subset of existing rights; knowledge of a PID, path, device name, service name, surface ID, or platform handle is not sufficient to authorize an operation.

## Typed SIP: native service IPC

SIP is the native typed service protocol layer. Contracts are written as C# interfaces and compiled into deterministic protocol metadata and runtime adapters by the SingPlus source generator.

The contract vocabulary includes attributes such as:

```text
[SipContract]
[Message]
[InitialState]
[Transition]
[RequiresCapability]
[Borrows]
[Consumes]
[ReturnsOwnership]
[BoundedPayload]
```

Generated artifacts include protocol definitions, dispatch/client transport metadata, capability requirements, response correlation, and ownership-aware request/response shapes.

The runtime validates protocol state, payload bounds, caller/session identity, capability requirements, and ownership transitions before publishing a state change.

### Service discovery and EndpointSession

Service discovery returns metadata; it does not grant invocation authority. The normal call path is:

```text
Resolve service
  -> verify contract identity
  -> open EndpointSession
  -> bind caller/provider process identities and generations
  -> validate session capability requirements
  -> invoke typed messages through the session
  -> close/cancel session lifecycle explicitly
```

`EndpointSession` is therefore the authority-bearing invocation context. Generic invocation/cancellation state is handled by the session runtime rather than being reimplemented separately by every service.

## Component admission and manifests

Components are admitted transactionally from deterministic manifests. `ServiceManifestV1` includes:

- component identity and version;
- SHA-256 image digest;
- `SingProcessManifestV1`;
- provided and required service contracts;
- typed platform requirements;
- local resource/capability requirements.

Platform requirements are semantic and versioned. Current feature families include neutral domains, owned-region mapping, I/O-domain binding, DMA mapping, explicit memory visibility, DSC1 bulk compute, MatrixTile/scoped accelerator support, virtualization/nested domains, evidence, secure domains, surface presentation, MMIO, IRQ, and execution policy.

Availability is explicit:

```text
ModelOnly
ProjectionOnly
RuntimeAdmission
Executable
ProductionSecure
```

Component startup acquires authority in a controlled order and unwinds in reverse when admission fails. A component does not become runnable merely because its image or manifest is valid; required services, capabilities, ownership state, and platform requirements must also be satisfied.

## Ownership-oriented memory model

SingNextOS treats memory ownership as a first-class authority dimension rather than reducing it to virtual-address mappings.

The memory model is built around:

```text
RegionId + RegionGeneration
RegionOwner
MutationEpoch
OwnedRegion<T> / OwnedBuffer<T>
BorrowLeaseHandle
RegionUseHandle
RegionBackingLeaseHandle
```

The important distinction is:

```text
mapping != ownership
visibility != ownership
completion != ownership return
```

### MOVE and BORROW

Large mutable SIP payloads use explicit ownership semantics:

- **MOVE / consume** transfers mutable ownership to the receiver and advances the authority state so the sender cannot keep using the old generation as writable authority.
- **BORROW** creates bounded temporary access while the original owner remains authoritative.
- ownership return is correlated with the operation/response that closes the temporary use.

This model is used by service IPC, compute, DMA/device access, virtualization guest memory, and GUI surface presentation.

### Region use and mutation epochs

The current region model additionally tracks independent `RegionUse` records and `MutationEpoch`. Region-use modes include:

```text
ReadOnly
ExclusiveWrite
StagedOutput
DirectCoherentWrite
DevicePrivate
SharedReadMostly
```

This makes device/accelerator/fabric access explicit without replacing the underlying `RegionAuthority`. `DirectCoherentWrite` exists as a semantic mode but is not the default publication policy; staged publication remains the conservative baseline where exact coherent-write guarantees are not proven.

## Common external-operation lifecycle

External work uses one explicit lifecycle rather than inferring state from a transport return value:

```text
Prepared
  -> Admitted
  -> Submitted
  -> DeviceComplete
  -> Visible
  -> Published
  -> Released
```

The operation record separately tracks disposition, dependency generations, visibility requirements, publication policy, effect class, replay protection, provider binding, and region uses.

Important non-equivalences are enforced by design:

```text
Admitted       != Submitted
Submitted      != DeviceComplete
DeviceComplete != Visible
Visible        != Published
Published      != Released
provider lost  != resources safely reclaimable
```

Release is authorized only after provider resources are closed or the external effect is independently proven contained. This lifecycle is reused by accelerator and CXL model paths instead of inventing subsystem-specific completion semantics.

## Platform Authority Bridge

`PlatformAuthorityBridge` is the privileged SingNextOS boundary that translates local OS identity/ownership into provider requests while keeping provider identities opaque to services.

The foundational `IPlatformAuthorityProvider` exposes narrow operations for:

- neutral domain binding/revocation;
- owned-region mapping/revocation.

Provider domain leases and mapping leases have independent provider IDs and generations. They are validated against the exact local `DomainId`, `ProcessHandle`, `RegionHandle`, owner, length, and access mode before they can influence local state.

The bridge is deliberately not a universal hardware abstraction layer. Higher-level families are expressed as narrow contracts owned by the subsystem that needs them.

## NeutralRuntime

The repository contains a physically separated HybridCPU NeutralRuntime contract/model stack:

```text
HybridCPU_NeutralRuntime.Contracts
HybridCPU_NeutralRuntime.AuthorityCore
HybridCPU_NeutralRuntime.Model
HybridCPU_NeutralRuntime.Tests
```

`INeutralDomainRuntime` composes semantic provider surfaces for:

- domain operations;
- region mappings;
- devices;
- MMIO;
- interrupts;
- DMA.

Additional contracts cover child domains, guest memory, virtual events/traps, bounded virtual I/O, and child execution artifacts.

NeutralRuntime defines **what the platform operation means**, not the concrete ISA, queue format, descriptor layout, or hardware topology used to implement it.

Two implementation profiles are distinguished explicitly:

```text
ModelOnly
ExecutableAdapter
```

That distinction is part of the feature-claim discipline: a model can prove lifecycle/authority semantics without being relabeled as executable hardware.

## HybridCPU-v2 integration

SingNextOS treats HybridCPU-v2 as an external execution/platform system rather than a source of OS authority.

The intended boundary is:

```text
SingNextOS local authority
  -> PlatformAuthorityBridge
  -> neutral semantic contract
  -> HybridCPU-specific adapter/provider
  -> HybridCPU ExternalRuntime
  -> ISE / future hardware implementation
```

The `HybridCpu_ExecutableAdapter` project is intentionally isolated. It consumes the versioned `HybridCPU.ExternalRuntime.Contracts` and `HybridCPU.ExternalRuntime` packages from the repository-local package feed and translates the supported executable **child-domain** profile into neutral child, guest-memory, executable-artifact, and bounded virtual-I/O contracts. It does not redefine the ordinary `INeutralDomainRuntime` authority root.

The rest of SingNextOS does not directly reference HybridCPU compiler, HCEXE inspector, ISE runner, lane, opcode, or internal runtime types.

## Device, MMIO, IRQ, and DMA authority

Device access is expressed through bounded semantic authority rather than ambient driver privilege.

A driver component receives a `DeviceResourceSet` composed from the exact capabilities and platform bindings admitted for that component. MMIO, IRQ, and DMA remain separate authority classes and use exact identities/generations.

DMA is modeled as multiple distinct steps:

```text
memory ownership / RegionUse
  -> mapping or DMA grant
  -> submit/admission
  -> device completion
  -> required visibility/acquire
  -> close/revoke grant
  -> ownership reuse/reclaim
```

A DMA grant is not a DMA completion; a completion notification is not authority; and a device-visible mapping is not proof that ownership may be returned.

## Compute and accelerators

Compute is exposed as a semantic typed service, not an opcode surface.

The current `IComputeService` demonstrates the ownership model directly:

```csharp
[SipContract, InitialState("Ready")]
public interface IComputeService
{
    [Message(1)]
    [Transition("Ready", "Ready")]
    [RequiresCapability(
        ResourceKind.Compute,
        CapabilityResourceIds.Dsc1Copy,
        CapabilityRights.Execute)]
    [ReturnsOwnership]
    ValueTask<OwnedBuffer<byte>> CopyAsync(
        [Borrows] OwnedBuffer<byte> source,
        [Consumes] OwnedBuffer<byte> destination);
}
```

The caller supplies temporary read authority over the source and transfers destination ownership to the service. The service/platform path manages the required mapping, operation lifecycle, completion, closure, and correlated ownership return. Platform mappings, provider identities, lanes, opcodes, and descriptors are intentionally absent from the service ABI.

Compute planning and provider selection are semantic: region-use compatibility, dependency generations, effect/publication policy, and requested service capability are evaluated above provider-specific execution details.

## Virtualization and nested domains

Virtualization is modeled as a neutral service over child domains rather than as a VMX-shaped kernel core.

The local architecture includes:

- `VirtualDomainAuthority` and `RuntimeKernel.Virtualization`;
- child-domain lifecycle and parent/child authority relationships;
- guest-memory mapping custody;
- virtual event sequencing;
- semantic trap contracts;
- bounded virtual I/O;
- executable-artifact admission for the explicit adapter profile;
- recursive teardown and generation checks.

Public SIP contracts expose `VirtualDomainHandle`/authority objects, not provider leases. A child receives a bounded subset of parent authority; provider IDs and compatibility projection state never become the source of local authority.

VMX/VMCS remains a possible downstream compatibility projection:

```text
neutral virtualization fact
  -> optional VMX/legacy projection
```

not:

```text
VMCS/legacy state
  -> native SingNextOS authority
```

## Evidence and SecureCompute

Evidence is a read-only observation plane. Evidence records may describe platform state, measurement, readiness, replay, or provider output, but they cannot mint capabilities, transfer region ownership, or publish effects.

SecureCompute admission requires an explicit feature class and exact current evidence/authority. `ProductionSecure` is intentionally stronger than ordinary runtime admission; providers must advertise only the level they can actually prove.

This keeps measurement/attestation semantics separate from the authority that creates or controls a secure domain.

## Native system services

SingNextOS uses source-familiar C# APIs over typed SIP rather than adopting Win32/POSIX as its native substrate.

Current native service contracts include filesystem, networking, and process-management surfaces. Object handles are session-bound and generation-bound, for example:

```text
FileObjectHandle   = EndpointSession + FileObjectId   + FileObjectGeneration
SocketObjectHandle = EndpointSession + SocketObjectId + SocketObjectGeneration
```

The source-facing layer is intended to feel idiomatic to .NET developers while preserving SingNextOS semantics underneath:

```text
source familiarity != binary compatibility
```

A familiar async method shape does not imply a Win32 handle, POSIX fd, Linux syscall, CoreCLR OS contract, or ambient process-wide authority.

## GUI, compositor, and presentation contracts

GUI is treated as a normal typed-service subsystem using the same capability and ownership ledger as the rest of the OS.

The contract model separates roles such as:

```text
Display
Compositor
WindowManager
Input
Clipboard
FontText
Accessibility
Notification
Shell
```

Surface identity is session-bound and generation-bound. Presentation supports explicit `Move` and `ReadLease` transfer modes, and completion is represented by a release fence rather than by assuming that a submitted frame is immediately reusable.

A desktop environment or compatibility personality may implement/project these services, but it does not define the native ABI. Global input, screen capture, clipboard, foreign-window access, display configuration, and similar cross-application operations are capability-scoped rather than ambient.

## CXL 3.x/4.x integration

CXL is integrated as a set of provider roles over the existing SingNextOS authority substrate rather than as a parallel memory/security model.

The architectural decomposition is:

```text
CXL.io    -> existing device / MMIO / IRQ / DMA authority
CXL.mem   -> memory discovery, placement, region backing, hotplug/reclaim
CXL.cache -> explicit coherent-access capability, not ownership
Fabric    -> topology/binding/reconfiguration evidence and materialized bindings
```

Current platform contracts keep endpoint/fabric/memory/coherent generations separate and expose semantic placement properties such as capacity, persistence, sharing, latency class, bandwidth class, and placement preference. Provider-private transport/topology identities do not enter ordinary application/SIP authority.

The software/model architecture includes:

- CXL endpoint discovery and semantic CXL.io projection;
- Type-3 memory capacity/placement/backing authority;
- staged Type-2 accelerator service integration;
- fabric binding, pooling, drain/rebind, and reconfiguration rules;
- security-readiness evidence;
- conservative multi-host sharing gates;
- common external-operation lifecycle and replay/effect classification.

The default accelerator publication model is staged. Coherent access can be a provider capability, but:

```text
coherence != ownership
coherence != publication
coherence != replay safety
coherence != authority
```

`DirectCoherentWrite` is therefore an explicit, stronger semantic mode rather than an assumed property of CXL-capable memory or accelerators.

## Compiler profiles, analyzers, generators, and admission verification

The repository uses build profiles to enforce architectural boundaries at compile/admission time.

`Directory.Build.props` enables analyzers and generators for Kernel, SIP, and Driver profiles. The kernel uses `KernelNoHeap`; analyzer/admission rules reject constructs such as managed allocation, capturing closures, `dynamic`, and forbidden host/runtime APIs on the admitted kernel root.

The admission tool emits deterministic proof metadata for the configured root/profile. This is producer/static evidence only: runtime authority is still revalidated at the point of use.

The build is configured for deterministic compilation and C# 13 on .NET 10.

## Repository layout

| Path | Purpose |
|---|---|
| `contracts/SingPlus.Contracts/` | Shared authority, manifest, protocol, memory, service, virtualization, GUI, and external-operation contracts |
| `src/Kernel/` | Privileged kernel and boot contour |
| `src/Runtime/SingPlus.Runtime/` | RuntimeKernel, capability/region/session/component/device/compute/virtualization/CXL authorities |
| `src/Sip/SingPlus.Sip/` | Product typed SIP contracts: compute, filesystem, networking, process, virtualization, GUI, etc. |
| `src/Drivers/SingPlus.Drivers/` | Driver-facing component model and typed driver services |
| `src/Platform/SingPlus.Platform.Abstractions/` | Provider-facing semantic platform contracts |
| `src/Platform/SingPlus.Platform.Host/` | Deterministic host/model platform provider |
| `src/Platform/SingPlus.Platform.HybridCpu/` | HybridCPU-neutral provider composition |
| `sdk/SingPlus.Analyzers/` | Architecture/profile Roslyn analyzers |
| `sdk/SingPlus.Generators/` | SIP/protocol source generators |
| `sdk/SingPlus.*.Sdk/` | Kernel/SIP SDK surfaces |
| `sdk/SingPlus.System/` | Source-facing native system library layer |
| `tools/SingPlus.Admission/` | Static admission verifier and proof generation |
| `tools/Runtime/HybridCPU_NeutralRuntime/` | NeutralRuntime contracts, authority core, model, tests |
| `tools/HybridCpu_ExecutableAdapter/` | Isolated HybridCPU executable child-domain adapter |
| `tools/SingPlus.HybridCpuQualification/` | Reproducibility/qualification record tooling |
| `tests/` | Runtime, architecture, provider, generator/analyzer, integration, and conformance tests |
| `docs/` | Whitebook, external requirements, CXL roadmaps, and cross-project design records |

## Build requirements

The repository pins the .NET SDK through `global.json`:

```text
.NET SDK 10.0.204
C# 13
```

HybridCPU ExternalRuntime packages used by the explicit executable adapter are stored in the repository-local `.packages` feed and mapped through `NuGet.Config`.

### Restore, build, and test

```bash
dotnet --version
dotnet restore SingNextOS.slnx --force --no-cache
dotnet build SingNextOS.slnx -c Release --no-restore
dotnet test SingNextOS.slnx -c Release --no-restore
```

The CXL software/model qualification record in `docs/cxl-refactoring-roadmap/13_FINAL_SOFTWARE_VALIDATION.md` documents a point-in-time full solution pass of 857/857 tests across the main SingPlus, HybridCPU platform, NeutralRuntime, and executable-adapter test projects.

### Optional HybridCPU qualification record

`eng/qualify-hybridcpu-aot.sh` builds the kernel/boot contour twice, verifies the `KernelNoHeap` admission root, records deterministic digests, and evaluates the explicitly configured HybridCPU integration prerequisite. The script enforces its own exact HybridCPU revision and should be read before use:

```bash
eng/qualify-hybridcpu-aot.sh /path/to/HybridCPU-v2
```

## Documentation

Start with the current architecture and status sources:

- [`docs/whitebook/hybridcpu-ise/00_README.md`](docs/whitebook/hybridcpu-ise/00_README.md) — HybridCPU/SingNextOS architecture whitebook and normative rules.
- [`docs/whitebook/hybridcpu-ise/02_PHILOSOPHY_AND_AUTHORITY_ALIGNMENT.md`](docs/whitebook/hybridcpu-ise/02_PHILOSOPHY_AND_AUTHORITY_ALIGNMENT.md) — intent/authority/evidence/publication model.
- [`docs/whitebook/hybridcpu-ise/03_DOMAIN_AND_CAPABILITY_ARCHITECTURE.md`](docs/whitebook/hybridcpu-ise/03_DOMAIN_AND_CAPABILITY_ARCHITECTURE.md) — domain/capability separation.
- [`docs/whitebook/hybridcpu-ise/04_MEMORY_OWNERSHIP_DMA_SECURE_IO.md`](docs/whitebook/hybridcpu-ise/04_MEMORY_OWNERSHIP_DMA_SECURE_IO.md) — memory ownership, DMA, visibility, and secure-I/O semantics.
- [`docs/whitebook/hybridcpu-ise/07_PLATFORM_BRIDGE_AND_EXTERNAL_CONTRACTS.md`](docs/whitebook/hybridcpu-ise/07_PLATFORM_BRIDGE_AND_EXTERNAL_CONTRACTS.md) — platform boundary and external contracts.
- [`docs/whitebook/hybridcpu-ise/12_NATIVE_API_AND_UI_CONTRACTS.md`](docs/whitebook/hybridcpu-ise/12_NATIVE_API_AND_UI_CONTRACTS.md) — native API and UI contract architecture.
- [`docs/external-requirements/README.md`](docs/external-requirements/README.md) — explicit cross-project/provider prerequisites and claim boundaries.
- [`docs/cxl-refactoring-roadmap/README.md`](docs/cxl-refactoring-roadmap/README.md) — current CXL 3.x/4.x SingNextOS architecture and software/model status.
- [`docs/hybridcpu-cxl-refactoring-roadmap/README.md`](docs/hybridcpu-cxl-refactoring-roadmap/README.md) — cross-project HybridCPU/CXL design direction.

Historical refactoring plans are useful for design lineage, but current source, tests, and the current status/evidence documents outrank stale phase prose.

## Design non-goals

SingNextOS deliberately does **not** define its native architecture around:

- a giant Unix/Win32-style syscall ABI;
- global ambient authority derived from names/IDs;
- a universal mutable shared-memory model;
- public physical-address/page-table/IOMMU/VMCS/lane/opcode contracts;
- a universal HAL that mirrors every hardware primitive into the kernel API;
- provider feature discovery as permission;
- completion receipts as capabilities;
- evidence/attestation as authority;
- VMX as the native virtualization object model;
- CXL coherence as an ownership or publication guarantee;
- zero-copy as a public semantic guarantee.

The preferred pattern is a narrow semantic contract with explicit authority, generation, ownership, completion, visibility, publication, and reclaim rules.

## Technical status boundary

The repository is best understood as a **systems-research OS/runtime architecture with executable software/model qualification and explicit provider profiles**, not as a production general-purpose OS distribution.

The implemented architecture is intentionally capable of hosting multiple backend classes without changing the public authority model:

```text
host/model backend
-> software executable adapter
-> external runtime / emulator
-> QEMU or device backend
-> FPGA / ASIC provider
```

Moving downward in that list should strengthen implementation evidence, not change the meaning of SingNextOS capabilities, ownership, sessions, regions, or publication semantics.

## License

SingNextOS is licensed under the **GNU Affero General Public License v3.0 (AGPL-3.0)**. See [`LICENSE.txt`](LICENSE.txt).