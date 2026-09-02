# 10. Source And Traceability

## Audit baselines

This WhiteBook revision is bound to these repository states:

```text
SingNextOS master
af791aba4e25615cef09b3933f34efca62296304
Merge PR #12: Runtime: add local Platform Authority Bridge and host provider

HybridCPU-v2 master
38bf0614d8a58e2543b4a956ccc23bb22e1a8170
docs fix2
```

Any later implementation work must re-audit current `master` rather than assuming these SHAs remain current.

## Evidence hierarchy

For present-tense claims this WhiteBook uses:

```text
current source behavior + executable tests
> current authoritative WhiteBook/activation classification
> generated inventory/conformance artifact
> historical research/vision material
> speculative architectural proposal
```

A lower layer may define target direction but cannot override a current denial, missing implementation or external-blocked status.

## SingNextOS current authority sources

| Area | Source | Proven fact used |
|---|---|---|
| kernel authority | `src/Runtime/SingPlus.Runtime/RuntimeKernel.cs` | process lifecycle, capability authority ownership, region/channel cleanup, active platform authority blocks terminate/fault |
| local domain grouping | `src/Runtime/SingPlus.Runtime/Domains/DomainRegistry.cs` | `DomainId` is local OS principal grouping, not raw HybridCPU domain handle |
| capability ledger | `src/Runtime/SingPlus.Runtime/Capabilities/CapabilityAuthority.cs` | subject, rights, generation, revocation epoch, subset delegation |
| capability contract | `contracts/SingPlus.Contracts/Capabilities.cs` | descriptor identity fields; current MMIO/IRQ/DMA wrappers |
| rights/resource taxonomy | `contracts/SingPlus.Contracts/KernelContracts.cs` | current semantic rights and current `ResourceKind` set |
| region identity | `contracts/SingPlus.Contracts/Regions.cs` | owner includes `DomainId + ProcessGeneration`; generation/state descriptors |
| region ownership | `src/Runtime/SingPlus.Runtime/Regions/RegionAuthority.cs` | transfer generation advance, borrow lease, mapping reservation interlock |
| typed channel runtime | `src/Runtime/SingPlus.Runtime/Channels/ChannelRegistry.cs` | capability/request/protocol checks before mutation, MOVE and borrow transport |
| protocol descriptors | `contracts/SingPlus.Contracts/Protocols.cs` | request payload classes, ownership metadata, deterministic protocol model |
| SIP source generation | `sdk/SingPlus.Generators/SingPlusGenerator.cs` | `[SipContract]` -> protocol, dispatcher, typed client transport, manifest/capability metadata |
| SIP async response metadata | `sdk/SingPlus.Generators/ResponsePayloadGenerator.cs` | generated response-shape metadata direction |
| analyzer | `sdk/SingPlus.Analyzers/SingPlusAnalyzer.cs` | contract/profile/ownership/capability static restrictions |
| admission verifier | `tools/SingPlus.Admission/AdmissionVerifier.cs` | whole-program reachable IL/profile restrictions and deterministic proof |

## Platform Authority Bridge current sources

| Area | Source | Proven fact used |
|---|---|---|
| provider contract | `src/Platform/SingPlus.Platform.Abstractions/PlatformAuthorityContracts.cs` | current features are only `NeutralDomainBinding` and `DirectOwnedRegionMapping`; opaque provider leases/generation; read/write mapping access |
| privileged bridge | `src/Runtime/SingPlus.Runtime/Platform/PlatformAuthorityBridge.cs` | separate local/provider identities, feature checks, stale/revoked/wrong-domain fail-closed validation |
| kernel integration | `src/Runtime/SingPlus.Runtime/Platform/RuntimeKernel.Platform.cs` | exact `MemoryRegion` capability + rights + owner/generation are validated before provider mapping; reservation rollback on provider failure |
| host reference provider | `src/Platform/SingPlus.Platform.Host/HostPlatformAuthorityProvider.cs` | deterministic host-only implementation of the two current feature bits |
| bridge tests | `tests/SingPlus.Tests/Platform/PlatformAuthorityBridgeTests.cs` | no local `CapabilityId` in provider API, capability-before-provider, stale/cross-domain rejection, mapping lifecycle interlocks |

### What these sources do not prove

The local bridge and host tests do not prove:

- a HybridCPU-backed provider;
- actual MMU/IOMMU/page-table remap;
- DMA queue/fence/drain semantics;
- CPU/device/GPU global coherence;
- display/GPU surface presentation;
- MatrixTile/DSC/L7 platform provider bindings;
- virtualization/evidence/SecureCompute provider implementation.

Documentation must keep those claims target/external-blocked/future until new source and integration evidence exists.

## Native API and UI traceability

[`12_NATIVE_API_AND_UI_CONTRACTS.md`](12_NATIVE_API_AND_UI_CONTRACTS.md) combines two evidence classes.

### Current implementation evidence

The following parts are grounded in current SingNextOS source:

- typed SIP contract generation;
- capability subject/resource/rights/generation/revocation;
- owned-region/buffer transfer and borrow;
- channel capability checks before mutation;
- local/host platform domain/region mapping seam;
- active platform mapping blocking incompatible region lifecycle.

### Target architecture evidence

The following parts come from the attached architecture materials and are accepted as **target direction**, not current implementation evidence:

- `.NET-like` native public API over typed SIP services;
- **source familiarity, not binary compatibility**;
- Win32/POSIX/Wine only as downstream compatibility/personality SIPs;
- standard UI contract subsystem independent of KDE/GNOME/toolkit ABI;
- Display/Compositor/Window Manager/Input/Clipboard/Font/Accessibility/Notification/Shell separation;
- GUI-specific least-authority concepts;
- surface `Present` ownership/grant transitions and double/triple buffering;
- general controlled `SharedGrant` abstraction;
- GPU/display/DMA ownership transition direction.

Repository search at this baseline does not find current `IWindowService`, `ICompositorService`, `SingPlus.UI` or `IFileSystem` implementations. Therefore `12` labels those names and services as target architecture.

## HybridCPU general architecture sources

| Area | Source | Fact used |
|---|---|---|
| runtime system shape | `Documentation/WhiteBook/3. system-overview.md` | neutral domains/runtime, typed slots, specialized execution planes |
| execution/publication | `Documentation/WhiteBook/5. execution-model.md` | capture/execution and publication/retire are distinct |
| typed-slot topology | `Documentation/WhiteBook/6. typed-slot-scheduling.md` | runtime-owned late lane binding |
| safety/legality | `Documentation/WhiteBook/10. safety-isolation-and-legality.md` | explicit legality and domain guard behavior |
| replay/determinism | `Documentation/WhiteBook/11. replay-determinism-and-evidence.md` | evidence-bounded determinism, not exact-cycle theorem |
| telemetry | `Documentation/WhiteBook/12. telemetry-and-measurement.md` | telemetry is evidence, not authority |
| boundaries/non-goals | `Documentation/WhiteBook/15. architectural-boundaries-and-non-goals.md` | no universal coherence/VMX-authority reinterpretation |
| current state | `Documentation/WhiteBook/17. current-state-and-modernization-tracks.md` | status classification for mainline/specialized features |
| opcode/lane inventory | `Documentation/ISE_Instructions_By_Lane_CodeConfirmed.md` | code-confirmed instruction/lane inventory only, not public OS ABI |

## HybridCPU virtualization sources

| Area | Source | Fact used |
|---|---|---|
| position | `Documentation/Virtualization WhiteBook/00_README.md` | VMX is compatibility frontend; neutral runtime owns authoritative state |
| authority | `Documentation/Virtualization WhiteBook/04_Authority_Model.md` | projection does not become mutation/authority |
| domain owners | `Documentation/Virtualization WhiteBook/05_Runtime_Domain_Owners.md` | execution/memory/I/O/event owner separation |
| capabilities/evidence | `Documentation/Virtualization WhiteBook/06_Capabilities_And_Evidence.md` | grant/evidence separation |
| memory/I/O | `Documentation/Virtualization WhiteBook/07_Memory_IO_Lanes.md` | neutral translation/IOMMU/DMA authority concepts |
| nested | `Documentation/Virtualization WhiteBook/08_Nested_Virtualization.md` | child-domain authority filtering; shadow compatibility state is projection |
| admission | `Documentation/Virtualization WhiteBook/11_Admission_Boundaries.md` | admission != backend/publication |
| completion/retire | `Documentation/Virtualization WhiteBook/12_Trap_Intercept_Completion_Retire.md` | completion/publication are separate gates |
| compiler/runtime | `Documentation/Virtualization WhiteBook/13_Compiler_ISA_Runtime_Contract.md` | encoded intent != live authority |
| invariants | `Documentation/Virtualization WhiteBook/15_Security_Invariants.md` | neutral-owner-first, host evidence non-leak |
| closure | `Documentation/Virtualization WhiteBook/16_Current_State_And_Closure_Matrix.md` | current/future compatibility status |

## HybridCPU SecureCompute sources

| Area | Source | Fact used |
|---|---|---|
| current status | `Documentation/SecureCompute WhiteBook/00_README.md` | activation hardening; no blanket production-positive claim |
| authority | `Documentation/SecureCompute WhiteBook/01_Architecture/01_Position_And_Authority.md` | neutral owner/admission/grant/backend/publication separation |
| descriptors | `Documentation/SecureCompute WhiteBook/01_Architecture/02_Runtime_Admission_And_Descriptors.md` | descriptor presence is not execution authority |
| private memory | `Documentation/SecureCompute WhiteBook/02_Policy_Domains/01_Memory_And_Private_Domains.md` | private/shared/measured policy requires real enforcement |
| evidence/grants | `Documentation/SecureCompute WhiteBook/02_Policy_Domains/02_Measurement_Evidence_And_Grants.md` | evidence != authority; grant-ledger closure caveats |
| secure I/O | `Documentation/SecureCompute WhiteBook/02_Policy_Domains/03_Secure_IO_And_Shared_Buffers.md` | buffer/token identity alone is not authority |
| migration | `Documentation/SecureCompute WhiteBook/02_Policy_Domains/04_Migration_Checkpoint_Restore.md` | current classification is not a complete secure migration protocol |

## HybridCPU stream/compute sources

| Area | Source | Fact used |
|---|---|---|
| plane taxonomy | `Documentation/Stream WhiteBook/00_README.md` | Stream/SRF/Matrix/DSC/L7 authority separation |
| Matrix state | `Documentation/Stream WhiteBook/03_MatrixTile/01_Architectural_Tile_State_And_Compute.md` | tile state is runtime architectural state, not a transferable OS memory token |
| Matrix transport | `Documentation/Stream WhiteBook/03_MatrixTile/02_Tile_Stream_Transport.md` | typed lane6/SRF transport, not generic zero-copy DMA |
| assists | `Documentation/Stream WhiteBook/04_Assists/00_README.md` | bounded warming only in current contour |
| DSC1 | `Documentation/Stream WhiteBook/DmaStreamCompute/01_Current_Contract.md` | scoped Copy/Add/Mul/Fma/Reduce, exact ranges, no DSC2/coherent-async overclaim |
| L7 | `Documentation/Stream WhiteBook/ExternalAccelerators/01_L7_SDC_Executive_Summary.md` | scoped accelerator command/commit contour |
| L7 conflicts | `Documentation/Stream WhiteBook/ExternalAccelerators/07_Memory_Conflict_Model.md` | model-local conflict handling is not universal CPU/device coherence |

## External requirement traceability

| Requirement | Current meaning |
|---|---|
| `EXT-HCPU-001` | external AOT/image/ISE qualification still required |
| `EXT-HCPU-002` | real console/timer/MMIO/IRQ/DMA platform binding still required |
| `EXT-HCPU-003` | local neutral binding abstraction now exists, but real HybridCPU provider is still external-blocked |
| `EXT-HCPU-004` | local exact owned-region mapping abstraction/interlock now exists, but real mapping/revocation/DMA semantics are external-blocked |
| `EXT-HCPU-005` | compute provider bindings remain external-blocked |
| `EXT-HCPU-006` | virtualization/nested/evidence/SecureCompute provider discovery remains external-blocked |

## Key decision-to-source map

| WhiteBook decision | Primary evidence |
|---|---|
| kernel stays privileged authority | `RuntimeKernel`, `CapabilityAuthority`, `RegionAuthority` |
| typed SIPs are native high-level service mechanism | generator + protocol/channel runtime |
| identifier knowledge is not authority | capability ledger and exact resource validation |
| ownership/MOVE preferred for large mutable IPC | `RegionAuthority`, `ChannelRegistry`, ownership payload contracts |
| controlled sharing only | current borrow lease + no current unrestricted shared-grant primitive |
| local bridge is current | platform abstraction/bridge/host/tests at `af791aba...` |
| HybridCPU DMA/remap/coherence is not current | no HybridCPU provider + external requirements 002–004 |
| GUI is standard target contract subsystem | attached architecture source + current absence of GUI contracts |
| GUI capabilities reuse existing ledger | current capability architecture; UI resource kinds remain target |
| surface `Present` follows ownership model | target derived from existing ownership semantics; no current surface/GPU backend |
| compatibility is downstream | HybridCPU compatibility/projection rules + Sing+ API architecture |
| no exact-cycle/global-coherence claim | HybridCPU current boundaries and external audit review |
| SecureCompute remains gated | current HybridCPU SecureCompute status + `EXT-HCPU-006` |

## Re-audit checklist

Before implementing a WhiteBook recommendation:

1. confirm current SingNextOS `master` SHA;
2. confirm current HybridCPU-v2 SHA if external behavior matters;
3. inspect exact local source/tests touched by the slice;
4. inspect current external status/non-goal document;
5. classify every requested feature as current, local/host-backed, external-blocked, target, projection-only or future-gated;
6. preserve capability/ownership negative tests;
7. do not infer a provider ABI from an internal ISE opcode/type;
8. do not modify HybridCPU/compiler/backend/ISE from a SingNextOS-only task unless explicitly scoped separately;
9. update external requirement if a real binding remains missing;
10. update this traceability file whenever current-source status changes.

## Decision

The source map is part of the architecture. Every claim about Sing+ native API, UI, zero-copy, DMA or HybridCPU integration must identify both the local authority mechanism and, where hardware is involved, the external provider/publication boundary.

If that trace does not exist, the correct status is **target / unproven / external-blocked / future-gated**, never “probably implemented”.