# EXT-HCPU-005

**Status:** Local DSC1 v1 + typed ComputeService ownership ingress + DMA mapping-use interlock / Host `ModelOnly`; HybridCPU executable binding `ExternalBlocked`

## Local boundary now implemented

SingNextOS has a narrow local DSC1 Copy v1 authority/lifecycle contour and a
separate generated product SIP ingress for the same semantic operation family.
The two boundaries are both current locally, but are **not yet composed into one
service-to-platform execution path**.

The platform/model contour provides:

- separate local `Dsc1ComputeCapability` with `Execute` authority;
- disjoint `OwnedBuffer<byte>` source/destination mappings and exact bounded
  equal-length ranges;
- bridge-private provider operation identity and public local continuation;
- exact typed completion/cancellation disposition composed with generic platform
  closure evidence;
- optional observation-driven generation-bound terminal wakeup through the
  existing `KernelEventEndpoint`, committed only after local output/custody
  settles;
- private bounded staging and runtime reservations for the Host `ModelOnly`
  reference copy;
- cancellation/capability revoke/process teardown that closes or pins the
  operation before mapping/domain/local reclaim;
- a bridge-private whole-mapping DMA↔DSC1 interlock that retains accepted,
  ambiguous or faulted uses until their exact release boundary;
- Host `Dsc1BulkCompute` v1 classified only as `ModelOnly`;
- HybridCPU provider DSC1 classified unavailable, with no implicit Host fallback.

The generated SIP ingress now provides:

- product contract `SingPlus.Sip.Compute.IComputeService`;
- exactly one operation, `CopyAsync`, over `OwnedBuffer<byte>`;
- exact caller-side `Compute / compute:dsc1-copy:v1 / Execute` capability;
- source `[Borrows]` read authority;
- destination `[Consumes]` exclusive MOVE authority;
- destination `[ReturnsOwnership]` through the existing correlated response
  ownership transport;
- a dedicated generated `OwnershipPair` request shape with exactly one Borrow
  and one Consume, rather than a generic variadic payload ABI;
- capability/request-shape and both RegionAuthority owner/generation/state
  preflights before ownership mutation;
- destination transfer-accessibility preflight before source loan acquisition;
- exact source-loan rollback if destination transfer subsequently fails;
- same-region source/destination rejection;
- requester destination-token invalidation and generation advance on MOVE;
- borrow lifetime invalidation and replay rejection on return;
- responder teardown that returns borrower-domain loans before reclaim,
  reclaims service-owned destination authority, and cancels unpublished
  correlated response state.

`RegionAuthority` remains the only source of truth for local memory ownership.
The request transport does not mint a second ownership ledger. Local
`CapabilityId`, region identity/generation, SIP request sequence, platform
mapping identity and provider/HybridCPU identities remain distinct namespaces.

## What Slice 4 does not prove

The generated `ComputeService` ingress currently stops at typed transport and
local authority movement. It does **not** itself:

- create or select a platform domain/mapping;
- call `SubmitPlatformDsc1Copy`;
- observe or cancel a platform DSC1 submission;
- bind the SIP source borrow to an external read grant;
- bind destination ownership to executable external write custody;
- prove output visibility from HybridCPU hardware;
- turn a Host CPU reference copy into accelerator evidence.

Therefore the existence of the product SIP does not upgrade Host `ModelOnly` or
the current `HybridCPU_NeutralRuntime` into executable DSC1 support.

The event-bearing platform observation also remains notification only. It is not
a provider receipt, compute authority, output-visibility proof or reclaim
proof. Waiter/endpoint cancellation does not cancel DSC1 or close provider
custody.

## Local cross-mechanism lifetime rule

The current bridge interlock uses the complete local
`PlatformRegionMapping` identity. Same-mapping read/read and non-overlapping
subranges are conservatively rejected across DMA and DSC1. Accepted lifetimes
on distinct independently authorized mappings may overlap, although admission
is coarse-serialized.

DMA completion does not release an active use until its required exact
post-completion visibility transition finishes. DSC1 releases only after exact
terminal settlement, local output publication/discard, buffer-reservation
release and exact local release commit. Ordinary pre-accept denial rolls back.
Malformed, faulted, thrown or ambiguous external state retains the exact use
where identifiable or quarantines the containing platform domain.

A prepared-but-unsubmitted DMA cycle is outside this active-use interlock.
Current owned buffers/pre-acquired managed aliases expose no mutation epoch that
would invalidate old prepare evidence after a later CPU or DSC1 write. A real
executable path therefore additionally needs a mutation/visibility epoch or a
mandatory fresh-prepare rule. This boundary is shared with `EXT-HCPU-004`.

## Exact external blocker

The audited HybridCPU integration still has no stable neutral semantic DSC1
facade that accepts the required neutral domain/mapping authority and returns
trustworthy submit, completion, cancellation/drain, custody and output-
visibility state suitable for SingNextOS ownership publication.

Internal ISE DSC1 descriptors, physical lane selection, raw opcodes, provider
tokens, host pointers, compiler types or compatibility state are not acceptable
substitutes. Evidence of an internal DSC1 contour is not external authority.

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
mapping-use and drain rules rather than trusting the SingNextOS local interlock
as hardware evidence.

Per project direction, this requirement records the missing external contract
only. This iteration makes no change to HybridCPU-v2, HybridCPU ISE or
`HybridCPU_Compiler_v2`.

## Required external capability

The external HybridCPU platform integration must expose a stable semantic
binding for the narrow operation actually required first: DSC1 bounded Copy.
Later MatrixTile, arithmetic/reduction DSC1 or scoped L7-SDC families remain
separate future slices and must not be pulled into this blocker opportunistically.

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
- truthful `Unavailable`/`Unsupported` classification when the executable
  contour is absent.

SingNextOS must not manually select a physical lane or construct raw ISE
operations to obtain this authority.

## Minimal reproduction for external closure

1. Receive exact SingNextOS source-read and destination-exclusive authority
   through the typed service boundary.
2. Bind only the required exact regions/ranges through the neutral platform
   memory/domain contract.
3. Discover executable DSC1 Copy support from the external provider.
4. Submit one bounded Copy without exposing a lane/opcode/provider token to the
   SIP caller.
5. Prove missing local capability, stale generation, wrong owner/domain/range
   and replay fail before external effect.
6. Prove malformed or ambiguous external success does not publish destination
   ownership and blocks lower-resource reclaim while external state is unsafe.
7. Prove cancellation/teardown drains or definitively closes accepted external
   work before source borrow return, destination ownership response, mapping
   close or local reclaim.
8. Prove terminal completion plus required output visibility is necessary before
   successful destination publication.
9. Prove unsupported executable support reports unavailable rather than Host
   fallback masquerading as HybridCPU execution.

## SingNextOS component blocked

HybridCPU-backed `System.Compute` / `ComputeService` executable DSC1 Copy and its
reusable output-custody/visibility boundary remain blocked.

The generated product `ComputeService` SIP ingress itself is no longer blocked:
it can transport exactly one source read-borrow plus one destination exclusive
MOVE/return pair with deterministic generated metadata, rollback and teardown
semantics. The next **local** slice is to compose that ingress with the existing
bounded `SubmitPlatformDsc1Copy` / observe / cancel lifecycle while retaining
Host `ModelOnly` and the external blocker. That composition is not evidence of
HybridCPU hardware execution.

## Explicit non-request

This requirement does **not** ask for:

- new HybridCPU opcodes;
- DSC2 implementation;
- Add/Mul/Fma/Reduce expansion in the current SingNextOS service;
- a universal external accelerator protocol;
- global CPU/device coherence;
- compiler/backend changes;
- SingNextOS control of HybridCPU lane allocation;
- provider or HybridCPU tokens in public/SIP ABI.

## Fallback/mock used

Host reference providers may implement the same semantic lifecycle for local
model tests. Provider classification must remain explicit: Host `ModelOnly` is
not ISE/hardware execution evidence. The current product ingress may also be
tested with a local service-model responder, but such a responder is only a
transport/ownership proof and does not satisfy this external requirement.
