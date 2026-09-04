# 10. Source And Traceability

## Audit baselines

This WhiteBook revision retains its historical audit baseline:

```text
SingNextOS master
af791aba4e25615cef09b3933f34efca62296304
Merge PR #12: Runtime: add local Platform Authority Bridge and host provider

HybridCPU-v2 master
38bf0614d8a58e2543b4a956ccc23bb22e1a8170
docs fix2
```

Later implementation status must be re-audited against current `master`. The
Phase-7 Slice-4 implementation was started from exact SingNextOS base
`c4baaa6e487c0f939be7ce530da0e17a9f243cca` and audited against HybridCPU-v2
`9e001bf29df06ad3d4ff7337f81d4e5bc0a62fc9`. The latter remains an external
architecture/integration source; Slice 4 changes no HybridCPU-v2, compiler or
ISE code.

## Evidence hierarchy

For present-tense claims this WhiteBook uses:

```text
current source behavior + executable tests
> current authoritative WhiteBook/activation classification
> generated inventory/conformance artifact
> historical research/vision material
> speculative architectural proposal
```

A lower layer may define target direction but cannot override a current denial,
missing implementation or external-blocked status.

## SingNextOS current authority sources

| Area | Source | Proven fact used |
|---|---|---|
| kernel authority | `src/Runtime/SingPlus.Runtime/RuntimeKernel.cs` | process lifecycle, capability authority ownership, region/channel cleanup, active platform authority blocks terminate/fault |
| local domain grouping | `src/Runtime/SingPlus.Runtime/Domains/DomainRegistry.cs` | `DomainId` is local OS principal grouping, not raw HybridCPU domain handle |
| capability ledger | `src/Runtime/SingPlus.Runtime/Capabilities/CapabilityAuthority.cs` | subject, rights, generation, revocation epoch, subset delegation |
| capability contract | `contracts/SingPlus.Contracts/Capabilities.cs`, `CapabilityResourceIds.cs` | descriptor identities and exact semantic DSC1 compute capability resource |
| rights/resource taxonomy | `contracts/SingPlus.Contracts/KernelContracts.cs` | semantic rights and `ResourceKind.Compute` without provider identity leakage |
| region identity | `contracts/SingPlus.Contracts/Regions.cs` | owner includes `DomainId + ProcessGeneration`; generation/state descriptors |
| region ownership | `src/Runtime/SingPlus.Runtime/Regions/RegionAuthority.cs` | transfer generation advance, borrow lifetime, stale/wrong-owner rejection and teardown reclaim |
| typed channel runtime | `src/Runtime/SingPlus.Runtime/Channels/ChannelRegistry.cs` | capability/request/protocol checks before mutation; single ownership transport plus exact Borrow+Consume pair admission/rollback |
| protocol descriptors | `contracts/SingPlus.Contracts/Protocols.cs` | deterministic request payload classes; `OwnershipPair` is exactly one Borrow plus one Consume, not variadic payload authority |
| product compute SIP | `src/Sip/SingPlus.Sip/Compute/IComputeService.cs` | one semantic `CopyAsync`: source `[Borrows]`, destination `[Consumes]`, destination `[ReturnsOwnership]`, exact Compute/Execute requirement |
| runtime SIP transport | `src/Runtime/SingPlus.Runtime/RuntimeSipClientTransport.cs`, `RuntimeKernel.Channels.cs` | generated client pair invocation reaches exact correlated request transport without hand-written provider identities |
| SIP source generation | `sdk/SingPlus.Generators/SingPlusGenerator.cs`, `ClientRuntimeAdapterGenerator.cs` | `[SipContract]` -> protocol/dispatcher/client metadata and dedicated two-slot ownership-pair runtime invocation |
| production generator wiring | `src/Sip/SingPlus.Sip/SingPlus.Sip.csproj` | product compute contract is compiled with the SingPlus generators as analyzers rather than duplicated generated code |
| SIP async response metadata | `sdk/SingPlus.Generators/ResponsePayloadGenerator.cs`, `ResponseProtocolGenerator.cs` | destination ownership response retains generated shape and correlated publication semantics |
| analyzer | `sdk/SingPlus.Analyzers/SingPlusAnalyzer.cs` | contract/profile/ownership/capability static restrictions |
| admission verifier | `tools/SingPlus.Admission/AdmissionVerifier.cs` | whole-program reachable IL/profile restrictions and deterministic proof |
| Slice-4 generator tests | `tests/SingPlus.Tests/Generators/OwnershipPairGeneratorTests.cs`, `OwnershipPairClientRuntimeAdapterTests.cs`, `GeneratorTests.cs` | deterministic pair metadata/client routing and fail-closed rejection of unsupported ownership cardinalities/shapes |
| Slice-4 runtime tests | `tests/SingPlus.Tests/Contracts/ComputeServiceOwnershipIngressTests.cs` | capability-first admission, stale/forged/wrong-owner/same-region/replay rejection, destination preflight, borrow rollback/return, ownership response and teardown cleanup |

## Platform Authority Bridge current sources

| Area | Source | Proven fact used |
|---|---|---|
| provider contract | `src/Platform/SingPlus.Platform.Abstractions/PlatformAuthorityContracts.cs` plus phase-specific provider contracts | opaque provider leases/generations; semantic feature families, not raw lanes/opcodes |
| privileged bridge | `src/Runtime/SingPlus.Runtime/Platform/PlatformAuthorityBridge.cs` | separate local/provider identities, feature checks and stale/revoked/wrong-domain fail-closed validation |
| kernel mapping integration | `src/Runtime/SingPlus.Runtime/Platform/RuntimeKernel.Platform.cs` | exact `MemoryRegion` capability + rights + owner/generation are validated before provider mapping; reservation rollback on provider failure |
| DSC1 lifecycle | `src/Runtime/SingPlus.Runtime/Platform/RuntimeKernel.PlatformDsc1.cs`, `PlatformAuthorityBridge.Dsc1.cs` | bounded UInt8/AllOrNone Copy Host `ModelOnly` lifecycle with exact submit/observe/cancel and fail-closed settlement |
| cross-mechanism memory-use admission | `src/Runtime/SingPlus.Runtime/Platform/PlatformAuthorityBridge.MappingUse.cs`, `RuntimeKernel.PlatformDmaSubmission.cs`, `RuntimeKernel.PlatformDmaPostCompletion.cs`, `RuntimeKernel.PlatformDsc1.cs` | conservative exact-mapping DMA↔DSC1 exclusion; ordinary pre-accept rollback; exact completion/visibility/terminal-settlement release; exact fault pinning or containing-domain quarantine |
| host reference provider | `src/Platform/SingPlus.Platform.Host/HostPlatformAuthorityProvider.Dsc1.cs` | DSC1 lifecycle is Host `ModelOnly`; provider has no direct region-content hardware effect |
| HybridCPU provider boundary | `src/Platform/SingPlus.Platform.HybridCpu` and its tests | neutral domains/other confirmed surfaces only; DSC1 executable feature remains unavailable/fail-closed |
| bridge tests | `tests/SingPlus.Tests/Platform/PlatformDsc1ComputeTests.cs`, `PlatformDmaDsc1MappingInterlockTests.cs` | provider denial/malformed/stale/cancel/teardown and mapping-use release boundaries |

### Slice-4 boundary between SIP and platform lifecycle

The following is current:

```text
caller Compute/Execute capability
+ source current OwnedBuffer<byte>
+ destination current OwnedBuffer<byte>
-> generated typed Borrow+Consume SIP admission
-> source BorrowLease to service
-> destination RegionAuthority MOVE to service
-> correlated destination ownership response is possible
```

Separately, the following is current:

```text
exact process + platform domain/mappings + Dsc1ComputeCapability
-> bounded Host ModelOnly SubmitPlatformDsc1Copy
-> exact Observe/Cancel closure
-> output/custody settlement
```

Slice 4 does **not** yet connect those two paths. Consequently a successful
local SIP transport test is not provider admission or HybridCPU execution
evidence. The next sequential local slice is the explicit composition of these
already implemented boundaries.

### What current sources do not prove

The local bridge, Host tests and generated ComputeService ingress do not prove:

- an executable HybridCPU DSC1 neutral facade;
- that a SIP request itself creates platform mappings or external grants;
- actual MMU/IOMMU/page-table remap for the compute buffers;
- executable DMA queue/fence/drain semantics;
- provider-side DMA↔DSC1 conflict enforcement or range/cache-line compatibility;
- an external custody transition corresponding to the SIP source borrow or
  destination MOVE;
- a CPU/managed-alias mutation epoch that invalidates stale DMA prepare evidence;
- CPU/device/accelerator global coherence;
- display/GPU surface presentation;
- MatrixTile/L7 platform provider bindings;
- virtualization/evidence/SecureCompute provider implementation.

Documentation must keep those claims target/external-blocked/future until new
source and integration evidence exists.

## Native API and UI traceability

[`12_NATIVE_API_AND_UI_CONTRACTS.md`](12_NATIVE_API_AND_UI_CONTRACTS.md) combines
two evidence classes.

### Current implementation evidence

The following parts are grounded in current SingNextOS source:

- typed SIP contract generation;
- capability subject/resource/rights/generation/revocation;
- owned-region/buffer transfer and borrow;
- generated exact Borrow+Consume request pairing for the bounded ComputeService
  contour;
- correlated ownership-return response publication;
- channel capability checks before ownership mutation;
- local/host platform domain/region mapping seam;
- active platform mapping/use blocking incompatible region lifecycle.

### Target architecture evidence

The following parts remain target direction, not current implementation
evidence:

- broader `.NET-like` native public API families over typed SIP services;
- **source familiarity, not binary compatibility**;
- Win32/POSIX/Wine only as downstream compatibility/personality SIPs;
- standard UI contract subsystem independent of KDE/GNOME/toolkit ABI;
- Display/Compositor/Window Manager/Input/Clipboard/Font/Accessibility/Notification/Shell separation;
- GUI-specific least-authority concepts;
- surface `Present` ownership/grant transitions and double/triple buffering;
- general controlled `SharedGrant` abstraction;
- GPU/display/DMA ownership transition direction.

The current ComputeService does not make Matrix/UI/filesystem/network service
names current by analogy.

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

The referenced HybridCPU ISE/compiler material is architecture and audit
background only. It is not accepted as current executable integration evidence.
Phase-7 Slices 1–4 neither consume nor change `HybridCPU_Compiler_v2` or ISE
implementation types. Slice 2 only projects exact local DSC1 terminal settlement
onto the existing generation-bound kernel event primitive. Slice 3 adds only a
SingNextOS bridge-private whole-mapping DMA↔DSC1 interlock. Slice 4 adds only
SingNextOS generated SIP/ownership transport and does not infer a provider ABI
from internal DSC1 types.

## External requirement traceability

| Requirement | Current meaning |
|---|---|
| `EXT-HCPU-001` | external managed-AOT/image/ISE path still required; local qualification remains reproducible but does not produce/execute a HybridCPU image |
| `EXT-HCPU-002` | real console/timer/MMIO/IRQ/DMA platform binding remains incomplete where no neutral executable interface exists |
| `EXT-HCPU-003` | exact neutral domain lifecycle exists, while later policy/provider families remain independently feature-gated |
| `EXT-HCPU-004` | local exact owned-region mapping plus conservative whole-mapping DMA↔DSC1 interlock exists; executable mapping/rebind/DMA, CPU mutation epoch and provider-side conflict semantics remain external-blocked |
| `EXT-HCPU-005` | bounded DSC1 Copy Host `ModelOnly` lifecycle, observation wakeup, DMA interlock and generated typed ComputeService Borrow+Consume ingress exist locally; ingress→platform DSC1 composition is the next local slice, while neutral executable HybridCPU compute/custody/visibility remains external-blocked |
| `EXT-HCPU-006` | virtualization/nested/evidence/SecureCompute provider discovery remains external-blocked/future-gated as classified |

## Key decision-to-source map

| WhiteBook decision | Primary evidence |
|---|---|
| kernel stays privileged authority | `RuntimeKernel`, `CapabilityAuthority`, `RegionAuthority` |
| typed SIPs are native high-level service mechanism | generator + protocol/channel runtime + product `IComputeService` |
| identifier knowledge is not authority | capability ledger and exact resource/generation validation |
| ownership/MOVE preferred for large mutable IPC | `RegionAuthority`, `ChannelRegistry`, ownership payload contracts |
| controlled sharing only | current borrow lease + no unrestricted shared-mutable grant |
| Borrow+Consume atomic request is narrow, not universal multi-payload ABI | `RequestPayloadKind.OwnershipPair` validation + generator negative tests |
| destination response is authority publication, not evidence | response ownership transport + `RegionAuthority.Transfer` generation advance |
| local bridge/model is separate from typed SIP ingress | ComputeService runtime tests vs platform DSC1 lifecycle tests |
| HybridCPU DSC1 execution is not current | `EXT-HCPU-005` + HybridCPU provider unavailable tests |
| no exact-cycle/global-coherence claim | HybridCPU current boundaries and external audit review |
| SecureCompute remains gated | current HybridCPU SecureCompute status + `EXT-HCPU-006` |
| compatibility remains downstream | HybridCPU compatibility/projection rules + Sing+ API architecture |

## Re-audit checklist

Before implementing a WhiteBook recommendation:

1. confirm current SingNextOS `master` SHA;
2. confirm current HybridCPU-v2 SHA if external behavior matters;
3. inspect exact local source/tests touched by the slice;
4. inspect current external status/non-goal document;
5. classify every requested feature as current, local/host-backed,
   external-blocked, target, projection-only or future-gated;
6. preserve capability/ownership negative tests;
7. do not infer a provider ABI from an internal ISE opcode/type;
8. do not modify HybridCPU/compiler/backend/ISE from a SingNextOS-only task
   unless explicitly scoped separately;
9. update external requirement if a real binding remains missing;
10. update this traceability file whenever current-source status changes;
11. do not treat generated SIP transport as proof of provider admission,
    completion, visibility or hardware execution.

## Decision

The source map is part of the architecture. Every claim about Sing+ native API,
zero-copy, DMA or HybridCPU compute integration must identify the local authority
mechanism and, where hardware is involved, the external provider/publication
boundary.

If that trace does not exist, the correct status is **target / unproven /
external-blocked / future-gated**, never “probably implemented”.
