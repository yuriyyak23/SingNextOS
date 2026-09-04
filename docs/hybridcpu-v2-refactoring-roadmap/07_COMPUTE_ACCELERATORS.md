# Phase 7 — Semantic compute and accelerator provider

## Status

**In progress — local Slices 1–4 complete; HybridCPU executable path remains
`ExternalBlocked`.** Depends on Phases 1–6 and corresponds to `EXT-HCPU-005`.

The local Phase-7 contour remains deliberately narrow: contiguous
`UInt8` / `AllOrNone` DSC1 `Copy` only, bounded to 1 MiB. `Add`, `Mul`, `Fma`,
`Reduce`, DSC2, queues, scatter/gather, overlapping copies and coherent async
execution are not part of the delivered contract.

## Delivered Slice 1 — bounded DSC1 Copy Host model

Slice 1 introduced the local/provider lifecycle for one bounded Copy operation:

```text
exact live Dsc1ComputeCapability(Execute, compute:dsc1-copy:v1)
+ exact local process generation
+ exact live v2 platform-domain binding
+ active source mapping with Read
+ active destination mapping with Write
+ bounded equal-length ranges
+ Dsc1BulkCompute v1 / ModelOnly feature
+ exact provider submission and Closed terminal receipt
```

`CapabilityId`, local submission identity, provider operation identity, domain
lease and mapping identities remain separate. Provider tokens never become SIP
capabilities or public ownership handles.

The Host provider advertises `Dsc1BulkCompute` only as `ModelOnly` and models
submit/completion/cancel lifecycle. `RuntimeKernel` performs a private staged CPU
reference copy and publishes output only after verified `Closed + Completed`.
`Closed + Cancelled` releases custody without publishing output. Pending,
denied, stale, malformed, faulted or ambiguous states cannot publish output and
retain or quarantine authority when closure is not proved.

This is not accelerator execution, zero-copy, coherent memory or ISE evidence.
A managed `Span<T>` acquired before runtime reservation remains outside the
revocable-custody guarantee.

## Delivered Slice 2 — generation-bound DSC1 observation wakeup

Slice 2 reuses the existing generation-bound `KernelEventEndpoint` for an
optional observation-driven completion notification. Endpoint capacity is
reserved before provider observation, terminal provider evidence is validated,
output/custody settles, and only then can the event commit.

The event is notification only. It is not compute authority, completion
receipt, provider closure, output-visibility evidence or reclaim authority.
Pending/error paths publish nothing, replay cannot publish a second event, and
waiter/endpoint cancellation does not substitute for DSC1 cancellation or
provider drain.

## Delivered Slice 3 — conservative DMA↔DSC1 mapping-use interlock

Slice 3 adds a bridge-private whole-mapping interlock without a new public
contract or parallel authority ledger:

```text
active or ambiguously accepted DMA use of mapping M
  conflicts with
active or ambiguously accepted DSC1 source/destination use of mapping M
```

The rule is intentionally conservative. Same-mapping read/read and disjoint
byte ranges still conflict. Distinct independently authorized mappings may have
overlapping accepted lifetimes, although local admission is coarse-serialized.

DMA retains its mapping use through exact post-completion visibility. DSC1
retains both uses through exact terminal closure, local output publication or
discard, buffer-reservation release and exact local release commit. Ordinary
pre-accept denial rolls back. Malformed, faulted, thrown or ambiguous external
state retains the exact use where identifiable or quarantines the containing
platform domain.

This is local admission policy only. It proves no provider-side conflict model,
IOMMU behavior, range/cache-line compatibility, CPU-alias mutation epoch or
cross-engine coherence.

## Delivered Slice 4 — generated typed ComputeService ownership ingress

Slice 4 adds the product-level semantic SIP ingress for the same bounded Copy
authority without composing it with the platform DSC1 lifecycle yet:

```csharp
[SipContract]
public interface IComputeService
{
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

The public contract exposes no platform mapping identity, provider lease,
HybridCPU token, lane, slot, opcode, descriptor, queue identity, physical
address or range implementation detail. `ValueTask` is logical SIP scheduling;
it does not claim asynchronous DSC1 hardware overlap.

### Exact request transport

The generated protocol now has one deliberately narrow two-slot ownership
shape, `OwnershipPair`. It is valid only when a message has exactly two
ownership-bearing parameters with exactly one `Borrow` and one `Consume`.
Ordinary zero/one-payload contracts remain unchanged. Two consumes, two borrows,
a bounded/primitive payload mixed with the pair, more than two parameters or an
ownership parameter without its lifecycle annotation fail generation or runtime
validation. This is not a variadic payload ABI or a generic tuple transport.

The generated runtime client routes the pair through the dedicated
`InvokeOwnershipPairAsync` transport. `SingPlus.Sip` explicitly wires the
source generators as an analyzer for the production contract so protocol,
response and runtime-client artifacts are built from the product interface,
not duplicated by hand.

### Authority and rollback order

For a valid request the local ordering is:

```text
validate response/capacity/protocol state
-> validate exact Compute/Execute capability
-> validate both ownership payload kinds
-> validate both current RegionAuthority owner/generation/state identities
-> reject source == destination region
-> preflight destination runtime transfer accessibility
-> acquire exact source BorrowLease for responder
-> transfer destination through RegionAuthority to responder
   and advance destination generation
-> enqueue exact correlated request
```

`RegionAuthority` remains the only ownership source of truth. No SIP transport
ledger can mint ownership by itself.

If destination transfer fails after source loan acquisition, the exact source
loan is revoked before the send returns failure. Capability, malformed shape,
stale generation, wrong owner, same-region alias and destination-reservation
failures happen before the corresponding visible ownership publication.

The source owner keeps ownership while its active borrow blocks new mutable
owner access through the current managed ownership API. Returning the borrow
invalidates its lifetime; replay fails. Destination MOVE invalidates the
requester's old token and advances the authoritative generation. Destination
ownership returns only through the existing typed response ownership transfer,
which advances authority again rather than treating the response payload as
mere evidence.

Responder teardown closes the channel/pending response, returns borrower-domain
loans before domain reclaim, and reclaims the service-owned destination rather
than making the requester's consumed token valid again. This proves absence of
premature local reclaim for this SIP transport contour.

Tests cover deterministic generation, invalid two-slot shapes, missing/forged/
wrong-right/wrong-resource compute capability, forged/stale region generation,
wrong owner, same-region source/destination, replay, destination runtime
reservation preflight, exact borrow return/replay, correlated destination
ownership return and responder teardown cleanup.

### Slice-4 non-claim

Slice 4 stops at typed SIP ingress. It does **not** call
`SubmitPlatformDsc1Copy`, create platform mappings, observe/cancel a DSC1
submission or claim that the Host/HybridCPU provider executed the request. The
existing Slice-1–3 platform/model contour remains separate until the next
vertical slice composes these already-proven boundaries.

HybridCPU-v2 therefore remains unchanged. The missing neutral executable DSC1
facade and executable custody/visibility proof remain `ExternalBlocked` under
`EXT-HCPU-005`.

## Goal

Expose one narrow, code-confirmed HybridCPU compute contour through semantic
Sing capabilities, owned regions and trustworthy completion without leaking
lanes/opcodes/runtime internals.

## Authority composition target

A future executable submission may proceed only when all authority layers are
live:

```text
caller/service compute capability
AND current source borrow + destination exclusive ownership
AND exact local process/resource generation
AND live opaque platform domain/mapping authority
AND provider compute feature at Executable level
AND provider custody/visibility requirements
AND HybridCPU runtime admission
-> external effect may proceed
```

Intent, authority, evidence and published state remain distinct.

## Memory rules

- immutable/read inputs use bounded read authority;
- mutable output remains single-writer/exclusive;
- CPU-visible output cannot publish before trustworthy terminal closure and
  required acquire/visibility semantics;
- failed/cancelled operations must settle custody before ownership return;
- no global coherence premise;
- ownership transfer does not promise zero-copy.

## Feature truthfulness

The provider must distinguish `ModelOnly`, `Executable`, unavailable and denied
states. A Host model may implement lifecycle semantics, but it must never be
reported as HybridCPU hardware execution. DSC2/queues/coherent overlap remain
unsupported until independently proven.

## HybridCPU-v2 changes expected

Only a stable exported semantic facade if an existing neutral interface does
not already provide the exact required submit/completion/cancel/custody and
visibility semantics. Internal ISE descriptors, lanes/opcodes or compiler types
are not an acceptable OS contract.

Do not add new opcodes, DSC2, a universal accelerator protocol, global
coherence or compiler lowering solely for this phase.

## Acceptance criteria

Phase 7 is complete only when one real HybridCPU compute operation consumes and
produces Sing-owned regions with exact authority, memory visibility,
trustworthy completion and teardown/revocation proof through a stable neutral
external facade.

Slices 1–4 do **not** satisfy that full-phase criterion. Slice 4 proves only the
semantic SIP authority ingress; Slices 1–3 prove the separate local/Host model
platform lifecycle. The stable neutral executable HybridCPU
submission/completion/cancel facade and executable output custody/visibility
remain isolated in `EXT-HCPU-005`.

## Next sequential slice — not started

After the Slice-4 PR is merged, the next local Phase-7 slice should compose the
existing generated `ComputeService` ingress with the already implemented
bounded DSC1 Copy lifecycle, without broadening the operation set:

```text
typed source Borrow + destination MOVE request
-> service-side exact local authority
-> required platform domain/mapping authority
-> existing SubmitPlatformDsc1Copy / Observe / Cancel lifecycle
-> terminal settlement + output publication/discard
-> close platform uses before lower-resource reclaim
-> return source borrow
-> publish destination ownership response
```

The composition must fail closed on stale/forged/replayed ownership,
wrong owner/domain/range/generation, unsupported provider feature, malformed or
ambiguous provider success, terminal fault and teardown. Neither source borrow
nor destination ownership may be returned while an accepted/ambiguous external
use can still exist. Executable HybridCPU remains `ExternalBlocked` unless a
real neutral facade independently satisfies that boundary.

## Do not do

- no raw lane 6/lane 7 API;
- no raw MicroOp/opcode construction in SIPs;
- no universal GPU/accelerator ABI;
- no implicit coherent shared buffers;
- no provider/HybridCPU token as capability;
- no operation-set expansion before the bounded Copy composition is closed.
