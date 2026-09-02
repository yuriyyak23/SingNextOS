# 10. Source And Traceability

## Audit baselines

This WhiteBook is bound to these repository states:

```text
SingNextOS master
3415327ebb822387eca18aef51251c9f658342a7
Merge PR #8: C4 prep: fail closed on generated response metadata

HybridCPU-v2 master
38bf0614d8a58e2543b4a956ccc23bb22e1a8170
docs fix2
```

Any later implementation work must re-audit current `master` rather than assuming these exact SHAs remain current.

## Provenance caveat

HybridCPU documentation itself contains an important provenance warning: some virtualization/SecureCompute architecture records were written against inspected worktree contours and activation-plan evidence, and the normative development-order source is the corresponding activation plan. Therefore this SingNextOS audit distinguishes:

- stable architectural principles explicitly documented in `master`;
- code-confirmed opcode/runtime inventories present in `master`;
- current/future closure classifications documented by HybridCPU;
- potential platform APIs that are **not** proven by those docs.

This WhiteBook never treats an internal HybridCPU source path as proof that a stable external SingNextOS integration ABI exists.

## SingNextOS authority sources

| Area | Source | Audit use |
|---|---|---|
| local kernel authority | `src/Runtime/SingPlus.Runtime/RuntimeKernel.cs` | process lifecycle, mint/delegate/revoke, domain cleanup |
| domain grouping | `src/Runtime/SingPlus.Runtime/Domains/DomainRegistry.cs` | proves current `DomainId` is local process grouping, not full ISE domain model |
| capability ledger | `src/Runtime/SingPlus.Runtime/Capabilities/CapabilityAuthority.cs` | rights, subject, generation, revocation epoch, delegation subset |
| region ownership | `src/Runtime/SingPlus.Runtime/Regions/RegionAuthority.cs` | owned/loaned/released lifecycle, generation transfer, borrow revocation |
| typed channel runtime | `src/Runtime/SingPlus.Runtime/Channels/ChannelRegistry.cs` | capability checks, request shape checks before mutation, ownership transfer/borrow |
| protocol descriptors | `contracts/SingPlus.Contracts/Protocols.cs` | deterministic typed request/ownership metadata and shape validation |
| capability types | `contracts/SingPlus.Contracts/Capabilities.cs` | local semantic MMIO/IRQ/DMA capability wrappers |
| manifest | `contracts/SingPlus.Contracts/Manifests.cs` | role/profile/resource requirements and canonical digest |
| driver manifest | `contracts/SingPlus.Contracts/DriverManifests.cs` | current narrow capability-declaration model |
| analyzer | `sdk/SingPlus.Analyzers/SingPlusAnalyzer.cs` | no-heap/ownership/SIP/capability restrictions |
| admission verifier | `tools/SingPlus.Admission/AdmissionVerifier.cs` | whole-program reachable IL/profile restrictions and deterministic proof |
| external boundary | `docs/external-requirements/EXT-HCPU-001.md` | AOT/image/ISE integration remains external |
| external HAL boundary | `docs/external-requirements/EXT-HCPU-002.md` | console/timer/MMIO/IRQ/DMA bindings remain external |

## HybridCPU general architecture sources

| Area | Source | Fact used |
|---|---|---|
| runtime system shape | `Documentation/WhiteBook/3. system-overview.md` | neutral domains/runtime, typed slots, separate specialized planes |
| execution/publication | `Documentation/WhiteBook/5. execution-model.md` | decode -> legality -> materialization -> execute -> retire; capture != publication |
| typed-slot topology | `Documentation/WhiteBook/6. typed-slot-scheduling.md` | W=8 class topology and runtime-owned late lane binding |
| safety/legality | `Documentation/WhiteBook/10. safety-isolation-and-legality.md` | explicit legality decisions, guard-before-reuse, domain isolation |
| replay/determinism | `Documentation/WhiteBook/11. replay-determinism-and-evidence.md` | evidence-bounded determinism, not global exactness theorem |
| telemetry | `Documentation/WhiteBook/12. telemetry-and-measurement.md` | telemetry as evidence, not authority |
| non-goals | `Documentation/WhiteBook/15. architectural-boundaries-and-non-goals.md` | no VMX backend authority, no universal L7/DSC/coherence, no CHERI reinterpretation |
| current matrix | `Documentation/WhiteBook/17. current-state-and-modernization-tracks.md` | mainline typed-slot, backend rename truthfulness, specialized feature status |
| opcode/lane inventory | `Documentation/ISE_Instructions_By_Lane_CodeConfirmed.md` | 215 code-confirmed instructions and lane-class placement |

## HybridCPU virtualization sources

| Area | Source | Fact used |
|---|---|---|
| source/authority position | `Documentation/Virtualization WhiteBook/00_README.md` | VMX is frozen compatibility frontend; neutral runtime is authority |
| authority model | `Documentation/Virtualization WhiteBook/04_Authority_Model.md` | descriptor-owned runtime admission, projection not mutation |
| domain owners | `Documentation/Virtualization WhiteBook/05_Runtime_Domain_Owners.md` | separate execution/memory/I/O/event owners |
| capabilities/evidence | `Documentation/Virtualization WhiteBook/06_Capabilities_And_Evidence.md` | grant-first capability intersection and evidence visibility |
| memory/I/O/lanes | `Documentation/Virtualization WhiteBook/07_Memory_IO_Lanes.md` | neutral translation, IOMMU/DMA and lane authorities |
| nested domains | `Documentation/Virtualization WhiteBook/08_Nested_Virtualization.md` | neutral child/nested model; shadow VMCS is projection |
| admission | `Documentation/Virtualization WhiteBook/11_Admission_Boundaries.md` | runtime admission is not backend/publication |
| completion/retire | `Documentation/Virtualization WhiteBook/12_Trap_Intercept_Completion_Retire.md` | backend, completion and retire are separate gates |
| compiler contract | `Documentation/Virtualization WhiteBook/13_Compiler_ISA_Runtime_Contract.md` | compiler intent/encoded carrier != runtime authority |
| security invariants | `Documentation/Virtualization WhiteBook/15_Security_Invariants.md` | neutral-owner-first, host evidence non-leak, SecureCompute not VMX-owned |
| closure matrix | `Documentation/Virtualization WhiteBook/16_Current_State_And_Closure_Matrix.md` | precise implemented/denied/future VMX status |
| roadmap/red flags | `Documentation/Virtualization WhiteBook/17_Roadmap_And_Residual_Risk.md` | future work must add neutral owners, not restore VMX authority |

## HybridCPU SecureCompute sources

| Area | Source | Fact used |
|---|---|---|
| current status | `Documentation/SecureCompute WhiteBook/00_README.md` | activation hardening, no current positive backend execution |
| authority position | `Documentation/SecureCompute WhiteBook/01_Architecture/01_Position_And_Authority.md` | required neutral owners/certificates/grant ledger/backend/publication separation |
| descriptor/admission | `Documentation/SecureCompute WhiteBook/01_Architecture/02_Runtime_Admission_And_Descriptors.md` | descriptor presence not execution; ordinary no-effect; non-ordinary fail-closed policy |
| private memory | `Documentation/SecureCompute WhiteBook/02_Policy_Domains/01_Memory_And_Private_Domains.md` | private/shared/measured/runtime-mutable policy, no production load/store/DMA enforcement proof |
| measurement/grants | `Documentation/SecureCompute WhiteBook/02_Policy_Domains/02_Measurement_Evidence_And_Grants.md` | evidence != authority; current secure grant ledger gap |
| secure I/O | `Documentation/SecureCompute WhiteBook/02_Policy_Domains/03_Secure_IO_And_Shared_Buffers.md` | buffer ID/token not authority; policy-only I/O result |
| migration | `Documentation/SecureCompute WhiteBook/02_Policy_Domains/04_Migration_Checkpoint_Restore.md` | current migration classifiers are not a secure checkpoint protocol |
| release proof | `Documentation/SecureCompute WhiteBook/03_Activation_Governance/02_Release_Conformance_And_Static_Guards.md` | narrative/tests/evidence classes do not replace production-path proof |

## HybridCPU stream/compute sources

| Area | Source | Fact used |
|---|---|---|
| plane taxonomy | `Documentation/Stream WhiteBook/00_README.md` | StreamEngine/SRF/BurstIO/Matrix/DSC/L7 authority separation |
| MatrixTile state | `Documentation/Stream WhiteBook/03_MatrixTile/01_Architectural_Tile_State_And_Compute.md` | architectural tile owner, compute capture and retire publication |
| MatrixTile transport | `Documentation/Stream WhiteBook/03_MatrixTile/02_Tile_Stream_Transport.md` | MTILE load/store stages through lane6/SRF, not direct generic DMA |
| assist plane | `Documentation/Stream WhiteBook/04_Assists/00_README.md` | bounded cache/SRF warming only; no memory commit/architectural publication |
| DSC1 | `Documentation/Stream WhiteBook/DmaStreamCompute/01_Current_Contract.md` | scoped Copy/Add/Mul/Fma/Reduce, exact ranges, synchronous helper, no DSC2/coherent async claim |
| L7 summary | `Documentation/Stream WhiteBook/ExternalAccelerators/01_L7_SDC_Executive_Summary.md` | scoped ACCEL_* command/guard/token/commit contour |
| L7 memory conflicts | `Documentation/Stream WhiteBook/ExternalAccelerators/07_Memory_Conflict_Model.md` | current conflict manager is model-local, not global CPU/device ordering authority |

## Previous Singularity+ WhiteBook source

The attached previous WhiteBook is used as a **vision input**, not implementation evidence. Its major idea clusters are:

- ownership/lifetimes and capability-only hardware access;
- SIP-only service decomposition and typed zero-copy IPC;
- Hypervisor-as-Firmware;
- Bartok-RS compiler/language extensions;
- UHDL driver-as-data and Manifest-to-Silicon;
- unified heterogeneous compute and virtual memory;
- .NET-like API and SIP tasks;
- deterministic debugging/snapshots/hot updates.

This audit reconciles those ideas in `08_DELTA_FROM_PREVIOUS_WHITEBOOK.md`.

## External audit source

A later external architectural audit proposed stronger interpretations around:

- DSC/Matrix zero-copy;
- one-cycle/near-free context switches;
- hardware capability/IOMMU checking;
- hard real-time exact cycles;
- assist-based GC/security scanning;
- L7 unified coherent memory;
- OS-as-verifier.

Those claims are reviewed individually in `11_EXTERNAL_AUDIT_CLAIM_REVIEW.md` and are not imported uncritically.

## Traceability from WhiteBook decisions to source evidence

| SingNextOS WhiteBook decision | Primary evidence class |
|---|---|
| keep kernel as privileged authority | current SingNextOS `RuntimeKernel`/authorities |
| keep capabilities/ownership as local OS primitives | current SingNextOS runtime/tests/contracts |
| add separate platform domain bindings | HybridCPU neutral domain owner architecture + current SingNextOS domain gap |
| no VMX/VMCS kernel manager | HybridCPU virtualization security invariants/non-goals |
| ownership zero-copy preferred | SingNextOS RegionAuthority + HybridCPU shared-buffer/domain rules |
| DSC as bulk offload, not zero-copy definition | DSC current contract |
| MatrixTile as distinct compute plane | MatrixTile state/transport docs |
| no exact-cycle RT claim | typed-slot dynamic rejects + replay determinism envelope |
| assists only for warming today | assist WhiteBook |
| no universal L7 coherent memory claim | L7 memory conflict model |
| SecureCompute feature-gated | SecureCompute current status/release gates |
| compiler/generator metadata not authority | HybridCPU compiler contract + SingNextOS runtime validation |

## Evidence hierarchy used by this audit

For current-state claims, use this precedence:

```text
current production/source behavior + executable tests
> current authoritative WhiteBook/activation classification
> generated inventory/conformance artifact
> historical research docs
> previous Singularity+ vision
> speculative external audit idea
```

A lower layer may inspire a future design but cannot override a higher layer's denial/current-state classification.

## Re-audit checklist for future sessions

Before implementing a WhiteBook recommendation:

1. confirm current `SingNextOS master` SHA;
2. confirm current `HybridCPU-v2 master` SHA if external behavior matters;
3. inspect the exact local implementation touched by the slice;
4. inspect current HybridCPU status/non-goal document for that contour;
5. classify the requested feature as local, external-blocked, code-confirmed, projection-only or future-gated;
6. implement one cohesive local slice;
7. add negative fail-closed tests;
8. do not modify HybridCPU/compiler/backend/ISE from SingNextOS work;
9. add/update external requirement if an external binding is still missing;
10. report what remains unproven.

## Decision

The source map is part of the architecture. SingNextOS should be able to explain every claimed HybridCPU integration feature in terms of a current local authority, a current external authority contract and a publication boundary. If that trace does not exist, the correct status is not “probably supported”; it is **unproven / external-blocked / future-gated**.