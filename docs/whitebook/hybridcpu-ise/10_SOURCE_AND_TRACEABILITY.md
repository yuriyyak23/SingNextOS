# 10. Source And Traceability

## Audit baselines

This WhiteBook retains its historical audit baseline for architectural context:

```text
SingNextOS master
af791aba4e25615cef09b3933f34efca62296304
Merge PR #12: Runtime: add local Platform Authority Bridge and host provider

HybridCPU-v2 master
38bf0614d8a58e2543b4a956ccc23bb22e1a8170
docs fix2
```

Later implementation status is always re-audited against current source. The
Phase-7 Slice-5 composition starts from exact merged SingNextOS base
`6bbdffeb82ea91ec14203b482557ab2ef4ea28d5` (PR #46 merged). External compute
classification remains grounded in the audited HybridCPU-v2
`9e001bf29df06ad3d4ff7337f81d4e5bc0a62fc9`; Slice 5 changes no HybridCPU-v2,
compiler or ISE code.

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
| kernel authority | `src/Runtime/SingPlus.Runtime/RuntimeKernel.cs` | process lifecycle, capability authority, region/channel cleanup and fail-closed platform sequencing |
| capability ledger | `src/Runtime/SingPlus.Runtime/Capabilities/CapabilityAuthority.cs` | subject, rights, generation, revocation and delegation remain local authority |
| semantic compute capability | `contracts/SingPlus.Contracts/Capabilities.cs`, `CapabilityResourceIds.cs` | `Dsc1ComputeCapability` wraps a local capability ID; exact resource remains `compute:dsc1-copy:v1` |
| region identity/ownership | `contracts/SingPlus.Contracts/Regions.cs`, `src/Runtime/SingPlus.Runtime/Regions/RegionAuthority.cs` | owner/generation, MOVE, borrow lifetime, stale/wrong-owner rejection and reclaim |
| typed request transport | `contracts/SingPlus.Contracts/Protocols.cs`, `src/Runtime/SingPlus.Runtime/Channels/ChannelRegistry.cs` | `OwnershipPair` is exactly one Borrow + one Consume and does not create a second ownership ledger |
| typed response transport | `src/Runtime/SingPlus.Runtime/Channels/ResponseRegistry.cs`, `RuntimeKernel.Responses.cs` | ownership return is correlated publication and transfers authority through `RegionAuthority` |
| product compute SIP | `src/Sip/SingPlus.Sip/Compute/IComputeService.cs` | exactly one `CopyAsync` with source `[Borrows]`, destination `[Consumes]`, `[ReturnsOwnership]`, caller Compute/Execute requirement |
| generated runtime client | `sdk/SingPlus.Generators/SingPlusGenerator.cs`, `ClientRuntimeAdapterGenerator.cs`, `src/Runtime/SingPlus.Runtime/RuntimeSipClientTransport.cs` | generated typed client routes the exact ownership pair through runtime transport without provider identities in SIP ABI |
| production generator wiring | `src/Sip/SingPlus.Sip/SingPlus.Sip.csproj` | product protocol/client artifacts are generated from the production contract |
| service composition | `src/Runtime/SingPlus.Runtime/Compute/RuntimeComputeServiceHost.cs` | typed request is composed with existing owner-bound platform DSC1 lifecycle through bounded service-owned source staging, exact cleanup and correlated response |
| Slice-4 tests | `tests/SingPlus.Tests/Contracts/ComputeServiceOwnershipIngressTests.cs`, generator pair tests | ingress capability/ownership validation, rollback, response ownership and generated deterministic transport |
| Slice-5 tests | `tests/SingPlus.Tests/Contracts/ComputeServiceDsc1CompositionTests.cs`, `ComputeServiceDsc1CompositionBoundaryTests.cs` | immediate/deferred Host completion, exact cancellation, bounded rejection with zero provider submit, internal service capability validation and post-cleanup domain revoke |
| analyzer/admission | `sdk/SingPlus.Analyzers/SingPlusAnalyzer.cs`, `tools/SingPlus.Admission/AdmissionVerifier.cs` | static/profile and reachable-IL restrictions remain independent proof layers |

## Platform Authority Bridge current sources

| Area | Source | Proven fact used |
|---|---|---|
| provider contracts | `src/Platform/SingPlus.Platform.Abstractions` | opaque provider leases, semantic feature families and exact lifecycle/status classes; no raw lane/opcode authority |
| privileged bridge | `src/Runtime/SingPlus.Runtime/Platform/PlatformAuthorityBridge*.cs` | local/provider identities remain separate; stale/revoked/wrong-domain/fault state fails closed |
| domain/mapping integration | `src/Runtime/SingPlus.Runtime/Platform/RuntimeKernel.Platform.cs` | platform mapping remains exact-process/owner-bound and capability-checked |
| DSC1 lifecycle | `src/Runtime/SingPlus.Runtime/Platform/RuntimeKernel.PlatformDsc1.cs`, `PlatformAuthorityBridge.Dsc1.cs` | bounded UInt8/AllOrNone Copy, exact submit/observe/cancel settlement and local use release |
| cross-mechanism interlock | `PlatformAuthorityBridge.MappingUse.cs`, `RuntimeKernel.PlatformDmaSubmission.cs`, `RuntimeKernel.PlatformDmaPostCompletion.cs`, `RuntimeKernel.PlatformDsc1.cs` | conservative exact-mapping DMA↔DSC1 exclusion and fail-closed fault pinning/quarantine |
| Host DSC1 provider | `src/Platform/SingPlus.Platform.Host/HostPlatformAuthorityProvider.Dsc1.cs` | lifecycle is `ModelOnly`; provider never becomes hardware/ISE evidence |
| HybridCPU provider | `src/Platform/SingPlus.Platform.HybridCpu` and integration tests | neutral domain/other confirmed surfaces only; executable DSC1 feature remains unavailable |
| platform tests | `tests/SingPlus.Tests/Platform/PlatformDsc1ComputeTests.cs`, `PlatformDmaDsc1MappingInterlockTests.cs` | provider denial/pending/cancel/malformed/teardown and exact mapping-use release rules |

## Phase-7 Slice-5 authority trace

### Public ingress

Current generated SIP admission is:

```text
caller exact Compute/Execute capability
+ caller-owned source OwnedBuffer<byte>
+ caller-owned destination OwnedBuffer<byte>
-> exact generated Borrow+Consume request
-> service receives BorrowLease<byte> source
-> service receives moved destination ownership
```

The source still belongs to the caller. The destination belongs to the service.
Knowledge of either region handle is not sufficient authority.

### Why direct platform composition is not used

Current platform region mappings and `SubmitPlatformDsc1Copy` are bound to one
exact process/platform subject. Mapping the caller-owned source as service-owned
platform authority would violate the current owner invariant. Slice 5 therefore
does not weaken the platform contract or reinterpret a borrow as ownership.

Instead:

```text
live caller source BorrowLease
-> snapshot exact bounded bytes
-> service-owned staging OwnedBuffer<byte>
-> exact service MemoryRegion(Map|Read) capability
-> exact staging Read mapping

service-owned destination
-> exact service MemoryRegion(Map|Write) capability
-> exact destination Write mapping
```

Both mapped buffers are then legitimately owned by the service subject used by
the existing DSC1 platform call.

### Existing DSC1 lifecycle reuse

Slice 5 adds no new provider operation or compute profile. It invokes the
existing:

```text
SubmitPlatformDsc1Copy
ObservePlatformDsc1Copy
CancelPlatformDsc1Copy
```

and relies on existing terminal validation/output settlement and bridge
mapping-use release.

### Successful publication order

The exact local order after `Closed + Completed` is:

```text
DSC1 terminal output settlement
-> close staging mapping
-> close destination mapping
-> revoke temporary mapping capabilities
-> release service staging region
-> return original source borrow
-> PublishResponse(service destination)
-> ResponseRegistry transfers destination back to original requester
   with a new authoritative region generation
```

Thus an accepted DSC1 use or live temporary mapping cannot coexist with returned
source borrow or successful destination ownership publication.

### Cancellation and ordinary rejection

On exact `Cancelled`, platform/mapping cleanup precedes source borrow return.
The correlated response is cancelled and the service-owned destination is
released locally; no successful ownership response is fabricated.

A bounded shape mismatch is rejected before any DSC1 provider submit. Resources
that were created before later ordinary denial are closed/revoked/released in
reverse authority order before borrow return and response cancellation.

### Ambiguous/faulted admission

A `PlatformFaulted` admission/result that may correspond to accepted external
work is not treated as ordinary denial. Without a trustworthy exact continuation,
Slice 5 does not close mappings through an invented identity, return the source
borrow, finalize the SIP response or release destination. Existing platform
fault pinning/domain quarantine remains the lower containment boundary.

## What current sources prove

The repository now proves the complete **local Host `ModelOnly` vertical path**:

```text
generated typed ComputeService call
-> exact capability + ownership IPC
-> bounded source staging under service ownership
-> exact temporary service platform mappings
-> existing Host ModelOnly DSC1 submit/observe/cancel
-> terminal cleanup
-> source borrow return
-> exact correlated destination ownership response or cancellation
```

The generated transport and platform lifecycle are no longer merely adjacent;
Slice 5 composes them in executable local tests.

## What current sources do not prove

They still do not prove:

- executable HybridCPU DSC1 feature discovery or submission;
- direct external consumption of the caller-owned borrowed source;
- cross-owner/cross-domain DSC1 provider authority;
- physical zero-copy/remap for the service composition;
- executable source/destination cache/custody semantics;
- CPU/device/accelerator global coherence;
- provider-side DMA↔DSC1 conflict enforcement or range/cache-line compatibility;
- a CPU/managed-alias mutation epoch for reusable executable mappings;
- Add/Mul/Fma/Reduce, DSC2 or queues in the product service;
- MatrixTile/L7 provider binding;
- virtualization/evidence/SecureCompute production implementation.

Documentation must keep these claims external-blocked/future until independent
source and integration evidence exists.

## Native API traceability

The product `IComputeService` is current evidence for the architectural rule:

```text
.NET-like semantic API
-> generated typed SIP
-> explicit local capability + ownership
-> privileged service/runtime composition
-> platform authority only behind the service boundary
```

It does not imply that filesystem/network/UI/Matrix services are implemented.
Those remain independently scoped.

The public ComputeService contains no provider lease, physical address, lane,
opcode, descriptor, queue or HybridCPU continuation. The service-side bounded
staging implementation is also not exposed as API semantics; callers observe
borrow/MOVE/return semantics, not copy-vs-remap implementation choice.

## HybridCPU architecture sources

HybridCPU WhiteBooks remain architecture and external-boundary sources. Relevant
current principles include:

- neutral runtime/domain authority precedes compatibility projections;
- execution/admission and publication/retire are separate gates;
- physical lane selection is runtime-owned and not an OS public API;
- evidence/telemetry is not authority;
- universal/global coherence is not assumed;
- internal DSC1 Copy/Add/Mul/Fma/Reduce inventory is code-confirmed breadth, not
  a stable SingNextOS executable provider ABI.

The internal DSC1/Matrix/L7 materials therefore cannot upgrade the local Host
composition to HybridCPU execution evidence.

## External requirement traceability

| Requirement | Current meaning |
|---|---|
| `EXT-HCPU-001` | external managed-AOT/image/ISE path still required; local qualification does not produce/execute a HybridCPU image |
| `EXT-HCPU-002` | real timer/remaining I/O/DMA executable surfaces remain independently blocked where no neutral interface exists |
| `EXT-HCPU-003` | exact neutral domain lifecycle is current; later policy/provider families remain separately feature-gated |
| `EXT-HCPU-004` | local exact mapping/visibility/interlock exists; reusable executable mapping/DMA mutation epoch/provider conflict semantics remain external-blocked |
| `EXT-HCPU-005` | complete local typed ComputeService→Host `ModelOnly` DSC1 Copy composition now exists; **real neutral executable HybridCPU DSC1 custody/visibility/submit/cancel/drain remains ExternalBlocked** |
| `EXT-HCPU-006` | virtualization/nested/evidence/SecureCompute provider discovery remains external-blocked/future-gated as classified |

## Key decision-to-source map

| WhiteBook decision | Primary evidence |
|---|---|
| kernel/runtime remain privileged authority | `RuntimeKernel`, `CapabilityAuthority`, `RegionAuthority` |
| typed SIP is native service mechanism | generator + channel/response runtime + product `IComputeService` |
| identifier knowledge is not authority | exact capability/resource/generation validation |
| MOVE/borrow remain local ownership semantics | `RegionAuthority`, `OwnershipPair` transport |
| bounded copy may adapt ownership semantics without violating them | `RuntimeComputeServiceHost` service-owned source staging |
| platform mapping remains owner-bound | `RuntimeKernel.Platform.cs`, bridge mapping validation |
| successful response cannot outrun platform use | Slice-5 cleanup order + composition tests |
| cancellation is not successful ownership publication | `RuntimeComputeServiceHost`, `ResponseRegistry`, cancellation test |
| Host ModelOnly is not HybridCPU execution | Host provider feature classification + HybridCPU fail-closed integration |
| executable DSC1 remains external | `EXT-HCPU-005` + no neutral executable DSC1 provider surface |
| no operation-set expansion by analogy | product `IComputeService` remains Copy only |

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
11. do not treat generated SIP transport, Host model completion or a local
    staging copy as proof of executable provider custody/visibility;
12. if one authority layer cannot legally cross into the next, adapt by copying
    or fail closed rather than forging ownership/grant equivalence.

## Decision

The source map is part of the architecture. Every claim about Sing+ native API,
zero-copy, DMA or HybridCPU compute integration must identify the local authority
mechanism and, where hardware is involved, the external provider/publication
boundary.

Slice 5 proves the local composition boundary end to end. It deliberately does
not erase the remaining external boundary. If real executable custody/visibility
trace does not exist, the correct status remains **ExternalBlocked**, never
“probably implemented”.
