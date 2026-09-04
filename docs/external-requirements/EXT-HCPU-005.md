# EXT-HCPU-005

**Status:** Local DSC1 v1 + typed ComputeService→DSC1 composition + DMA
mapping-use interlock / Host `ModelOnly`; HybridCPU executable binding
`ExternalBlocked`

## Local boundary now implemented

SingNextOS now composes the generated product `ComputeService` ingress with the
existing bounded DSC1 Copy v1 Host-model lifecycle. The local vertical path is:

```text
caller Compute/Execute capability
+ caller-owned source -> exact SIP read BorrowLease
+ caller-owned destination -> exact SIP MOVE to service
-> bounded service-owned source staging
-> service platform domain + exact temporary source/destination mappings
-> existing SubmitPlatformDsc1Copy / Observe / Cancel
-> exact terminal settlement
-> close both platform mappings
-> revoke temporary mapping capabilities
-> release staging
-> return original source borrow
-> Completed: publish destination ownership response
   Cancelled/ordinary pre-submit failure: cancel response + release service destination
```

The staging step is deliberate. Current platform mappings and DSC1 submission
are owner-bound to one exact platform-domain subject, while the typed SIP source
remains owned by the caller and is only borrowed by the service. The local
composition therefore does **not** invent cross-owner provider authority or
reinterpret a borrow as ownership. It snapshots the bounded source bytes into a
service-owned buffer and keeps the original borrow alive until accepted platform
use and temporary mappings are closed.

This is an authority-preserving composition, not a zero-copy claim. The Host
provider remains `ModelOnly`; `RuntimeKernel` still owns the local reference-copy
publication semantics. The original caller source is not advertised as being
directly consumed by an accelerator or external execution domain.

The complete local contour now provides:

- product `SingPlus.Sip.Compute.IComputeService.CopyAsync` only;
- caller-side exact `Compute / compute:dsc1-copy:v1 / Execute` requirement;
- source `[Borrows] OwnedBuffer<byte>` and destination `[Consumes]` +
  `[ReturnsOwnership]`;
- narrow generated `OwnershipPair` transport with exactly one Borrow and one
  Consume;
- exact RegionAuthority owner/generation/state validation and destination MOVE;
- one service-side `Dsc1ComputeCapability` independently validated for the
  platform call;
- bounded equal full-buffer Copy only, with the existing DSC1 v1 maximum;
- exact temporary service memory capabilities and owner-bound platform mappings;
- existing DSC1 submit/observe/cancel identity and terminal validation;
- existing bridge-private whole-mapping DMA↔DSC1 interlock;
- close-before-borrow-return and close-before-response-publication ordering;
- response ownership return only through the existing correlated response
  transfer, advancing authoritative destination generation;
- cancellation that returns the source borrow only after platform closure, does
  not publish destination as success, and locally releases the service-owned
  destination;
- ordinary bounded/pre-submit rejection that performs no DSC1 provider submit,
  returns the source borrow, cancels the correlated response and releases the
  consumed destination;
- ambiguous/faulted provider admission that remains fail-closed: when exact
  accepted external work cannot be reconciled, the service does not return the
  source borrow, cancel/publish the response or release destination through the
  uncertain platform lifetime;
- Host `Dsc1BulkCompute` v1 classified only as `ModelOnly`;
- HybridCPU provider DSC1 classified unavailable, with no implicit Host fallback.

`RegionAuthority` remains the only source of truth for local memory ownership.
SIP request sequence, local capability IDs, region generations, platform mapping
IDs, local DSC1 submission IDs and provider/HybridCPU identities remain distinct
namespaces.

## What the local composition does not prove

The composed service path does **not** prove:

- direct external use of the caller-owned borrowed source region;
- cross-owner/cross-domain DSC1 submission;
- physical zero-copy or remap;
- accelerator/ISE execution;
- executable HybridCPU output custody or cache visibility;
- global CPU/device/accelerator coherence;
- provider-side DMA↔DSC1 conflict enforcement;
- a CPU-alias mutation epoch suitable for reusable executable mappings.

The source snapshot is a local bounded-copy adaptation required by the current
owner-bound platform contract. It is not evidence that a future executable
provider can safely consume a borrowed caller mapping directly.

The existing optional `KernelEventEndpoint` DSC1 notification remains
observation-driven notification only. It is not compute authority, provider
closure, output visibility, ownership return or reclaim proof.

## Local cross-mechanism lifetime rule

The bridge interlock continues to use the complete local
`PlatformRegionMapping` identity. Same-mapping read/read and non-overlapping
subranges are conservatively rejected across DMA and DSC1. Accepted lifetimes
on distinct independently authorized mappings may overlap, although admission
remains coarse-serialized.

DMA releases active use only after its required exact post-completion visibility
transition. DSC1 releases only after exact terminal settlement, local output
publication/discard, buffer-reservation release and exact local release commit.
Malformed, faulted, thrown or ambiguous external state retains the exact use
where identifiable or quarantines the containing platform domain.

The new ComputeService composition closes its temporary mappings only after this
DSC1 release boundary. The original source borrow is returned only after those
mapping closures succeed. Thus response/caller authority cannot outrun accepted
platform use.

A prepared-but-unsubmitted DMA cycle is still outside the active-use interlock.
Current managed aliases expose no mutation epoch that invalidates old prepare
evidence after a later CPU/DSC1 write. Real executable reuse still needs an
external mutation/visibility epoch or mandatory fresh-prepare rule shared with
`EXT-HCPU-004`.

## Exact external blocker

The audited HybridCPU integration still has no stable neutral semantic DSC1
facade that accepts the required neutral domain/mapping authority and returns
trustworthy submit, completion, cancellation/drain, custody and output-
visibility state suitable for SingNextOS ownership publication.

Internal ISE DSC1 descriptors, physical lane selection, raw opcodes, provider
tokens, host pointers, compiler types or compatibility state are not acceptable
substitutes. Evidence that an internal DSC1 contour exists is not external
authority.

An executable facade must prove at least:

```text
exact live neutral subject/domain authority
AND exact source/destination memory authority
AND exact operation/range/profile admission
AND bounded accepted-work identity
AND cancellation/drain semantics
AND exact terminal completion disposition
AND output custody/CPU-visibility transition
AND stale/replay/wrong-generation rejection
AND close-before-rebind/reclaim
```

It must also compose or independently enforce equivalent cross-engine
mapping-use and drain rules rather than treating the SingNextOS local interlock
as hardware evidence.

Per project direction, this requirement records the missing external contract
only. This iteration makes no change to HybridCPU-v2, HybridCPU ISE or
`HybridCPU_Compiler_v2`.

## Required external capability

The external HybridCPU platform integration must expose a stable semantic
binding for the narrow operation actually required first: bounded DSC1 Copy.
Later MatrixTile, Add/Mul/Fma/Reduce DSC1 and scoped L7-SDC remain separate
future work and must not be pulled into this blocker opportunistically.

For DSC1 Copy the neutral external surface must support:

- exact neutral domain/subject binding;
- exact admitted source read and destination write memory authority;
- bounded range/type/profile validation;
- all-or-none completion semantics where actually guaranteed;
- exact accepted operation identity with independent generation/lifetime;
- observation and cancellation/drain;
- malformed/stale/wrong-domain/wrong-generation rejection;
- explicit CPU output-visibility/custody settlement before mapping reuse or
  ownership publication;
- truthful `Unavailable`/`Unsupported` classification when executable support is
  absent.

SingNextOS must not manually select a physical lane or construct raw ISE
operations to obtain this authority.

## Minimal reproduction for external closure

1. Receive exact SingNextOS source-read and destination-exclusive authority
   through the typed service boundary.
2. Adapt or bind only exact ranges permitted by the eventual neutral memory
   contract; do not infer that a CPU borrow is directly executable authority.
3. Discover executable DSC1 Copy support from the external provider.
4. Submit one bounded Copy without exposing lane/opcode/provider identity to the
   SIP caller.
5. Prove missing local capability, stale generation, wrong owner/domain/range
   and replay fail before external effect.
6. Prove malformed or ambiguous external success cannot publish destination
   ownership and blocks lower-resource reclaim while external state is unsafe.
7. Prove cancellation/teardown drains or definitively closes accepted external
   work before source borrow return, destination response, mapping close or
   local reclaim.
8. Prove terminal completion plus required output visibility is necessary before
   successful destination publication.
9. Prove absent executable support reports unavailable rather than Host fallback
   masquerading as HybridCPU execution.

## SingNextOS component blocked

The local product `ComputeService`→Host-model DSC1 Copy path is no longer
blocked. It now proves the complete Sing-local composition from typed ownership
IPC through existing platform lifecycle and back to correlated ownership
publication.

What remains blocked is **HybridCPU-backed executable DSC1 Copy** and its
reusable neutral source/destination custody and output-visibility contract.
The local bounded staging adaptation does not remove that external requirement
and must not be used to relabel Host `ModelOnly` as executable hardware support.

## Explicit non-request

This requirement does **not** ask for:

- new HybridCPU opcodes;
- DSC2 implementation;
- Add/Mul/Fma/Reduce expansion in the current SingNextOS service;
- cross-owner provider submission invented solely to avoid bounded staging;
- a universal external accelerator protocol;
- global CPU/device coherence;
- compiler/backend changes;
- SingNextOS control of HybridCPU lane allocation;
- provider or HybridCPU tokens in public/SIP ABI.

## Fallback/mock used

`HostPlatformAuthorityProvider` supplies deterministic `ModelOnly` lifecycle for
local composition tests. `RuntimeComputeServiceHost` uses service-owned bounded
staging to bridge the SIP borrow/MOVE authority shape to the current owner-bound
platform contract. Both are local reference behavior only; neither is ISE or
HybridCPU hardware execution evidence.
