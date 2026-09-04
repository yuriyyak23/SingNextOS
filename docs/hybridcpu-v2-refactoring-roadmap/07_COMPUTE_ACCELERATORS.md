# Phase 7 — Semantic compute and accelerator provider

## Status

**In progress — local Slices 1–5 complete; HybridCPU executable path remains
`ExternalBlocked`.** Depends on Phases 1–6 and corresponds to `EXT-HCPU-005`.

The delivered local Phase-7 contour remains deliberately narrow: contiguous
`UInt8` / `AllOrNone` DSC1 `Copy` only, bounded to the existing 1 MiB contract.
`Add`, `Mul`, `Fma`, `Reduce`, DSC2, queues, scatter/gather, overlapping copies
and coherent async execution are not part of the delivered service.

## Delivered Slice 1 — bounded DSC1 Copy Host model

Slice 1 introduced one exact local/provider lifecycle:

```text
exact live Dsc1ComputeCapability(Execute, compute:dsc1-copy:v1)
+ exact local process generation
+ exact live platform-domain binding
+ source mapping Read
+ destination mapping Write
+ bounded equal-length ranges
+ Dsc1BulkCompute v1 / ModelOnly
+ exact provider submission and Closed terminal receipt
```

The Host provider models submit/completion/cancel only. `RuntimeKernel` owns the
private staged CPU reference copy and publishes output only after verified
`Closed + Completed`; cancelled work releases custody without publishing output.
Pending, denied, malformed, faulted or ambiguous state cannot publish output and
retains or quarantines authority when exact closure is not proved.

This is not accelerator execution, physical zero-copy, coherent memory or ISE
evidence.

## Delivered Slice 2 — generation-bound DSC1 observation wakeup

Slice 2 reuses the existing generation-bound `KernelEventEndpoint` for optional
observation-driven terminal notification. Endpoint capacity is reserved before
provider observation, exact terminal evidence is validated, output/custody
settles, and only then can the event commit.

The event is notification only. It is not compute authority, provider closure,
output visibility, ownership return or reclaim authority. Waiter/endpoint
cancellation does not substitute for DSC1 cancellation or provider drain.

## Delivered Slice 3 — conservative DMA↔DSC1 mapping-use interlock

Slice 3 adds a bridge-private whole-mapping interlock without a second authority
ledger:

```text
active or ambiguously accepted DMA use of mapping M
  conflicts with
active or ambiguously accepted DSC1 source/destination use of mapping M
```

Same-mapping read/read and disjoint byte ranges remain conflicts. DMA releases
only after exact completion plus required post-completion visibility; DSC1
releases after exact terminal settlement, local publication/discard, managed
reservation release and exact local release commit. Faulted/ambiguous state
retains use or quarantines its containing domain.

This proves local admission ordering only, not provider-side conflict handling,
IOMMU behavior, cache-line compatibility or global coherence.

## Delivered Slice 4 — generated typed ComputeService ownership ingress

Slice 4 adds the product semantic SIP contract:

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

The generated transport supports one deliberately narrow `OwnershipPair` shape:
exactly one Borrow and one Consume. `RegionAuthority` remains the only ownership
source of truth. Capability/request-shape, owner/generation/state and destination
transfer-accessibility checks precede visible ownership mutation; same-region
source/destination is rejected; destination MOVE advances generation and
invalidates the caller's old token; source borrow return invalidates its exact
lifetime; destination returns only through the existing correlated ownership
response transfer.

No platform/provider/HybridCPU identity, physical address, lane, opcode,
descriptor or queue identity enters the SIP ABI.

## Delivered Slice 5 — typed ComputeService → bounded DSC1 lifecycle composition

Slice 5 closes the next local vertical boundary by composing Slice 4 with the
existing Slice-1 lifecycle through `RuntimeComputeServiceHost`.

### Why a bounded service-owned source staging buffer is required

After typed SIP delivery the authorities intentionally differ:

```text
source region      -> still owned by caller; service holds BorrowLease<byte>
destination region -> moved exclusively to service
```

Current platform mappings are owner-bound to one exact platform-domain subject,
and current `SubmitPlatformDsc1Copy` requires both mapped buffers under the same
subject. Directly mapping the caller-owned source under the service subject
would violate the existing owner/platform invariant; pretending the borrow is
ownership would create a second authority path.

The composition therefore uses an explicit bounded adaptation:

```text
live caller source BorrowLease
-> copy exact bounded source bytes into service-owned staging buffer
-> keep original source borrow live
-> map service staging Read
-> map service-owned destination Write
-> existing SubmitPlatformDsc1Copy
```

The staging copy is semantic adaptation, not an optimization claim. It is
compatible with the standing rule that MOVE/borrow semantics do not promise
physical zero-copy.

### Normal successful order

For one admitted request:

```text
typed caller Compute/Execute check
-> source Borrow + destination MOVE through generated SIP
-> service validates its independent DSC1 Compute capability
-> validate equal positive bounded lengths
-> allocate service-owned source staging
-> snapshot exact borrowed source bytes
-> mint exact temporary MemoryRegion(Map|Read) for staging
-> mint exact temporary MemoryRegion(Map|Write) for destination
-> map both under exact service PlatformDomainBinding
-> existing SubmitPlatformDsc1Copy
-> ObservePlatformDsc1Copy until exact Closed + Completed
-> existing RuntimeKernel output publication into service destination
-> close staging mapping
-> close destination mapping
-> revoke temporary mapping capabilities
-> release staging region
-> return original source borrow
-> PublishResponse(destination)
   RegionAuthority transfers destination back to original requester
   and advances generation again
```

Neither the caller source nor destination ownership response is returned while
an accepted platform DSC1 use or either temporary platform mapping remains
active.

### Pending and exact cancellation

If provider observation is pending, the host retains the request, original
source borrow, service destination, temporary mappings and exact local DSC1
submission identity. The typed client remains pending.

Explicit service cancellation uses the existing `CancelPlatformDsc1Copy`
identity. Only after exact terminal `Cancelled` settlement and mapping closure
does the host:

```text
release staging
-> return original source borrow
-> cancel correlated SIP response
-> release service-owned destination locally
```

Cancellation never publishes destination as a successful ownership response and
never resurrects the caller's consumed destination token.

### Ordinary pre-submit rejection

Equal-length/bound checks happen before platform submission. Later ordinary
mapping/provider denial also follows exact cleanup of every successfully created
local/platform resource. After safe cleanup the source borrow returns, the
response is cancelled and the service-owned destination is released. Tests
prove a mismatched bounded shape makes **zero** DSC1 provider submit calls.

### Ambiguous/faulted provider admission

`PlatformFaulted` is not treated as ordinary rejection. If accepted external
work may exist without a trustworthy exact continuation, the composition stays
pinned: it does not close mappings through an invented identity, return the
source borrow, cancel/publish the response or release destination through
uncertain external state. Existing bridge/domain quarantine remains the lower
fail-closed containment mechanism.

### Slice-5 proof

End-to-end tests drive the generated runtime client, not a manually synthesized
platform call. They cover:

- immediate Host `ModelOnly` completion and exact ownership return;
- deferred provider completion: caller source and response remain blocked until
  exact terminal observation;
- exact cancellation: platform use closes before source borrow return and the
  ownership response is cancelled;
- unequal source/destination lengths: cancellation and cleanup with no DSC1
  provider submission;
- wrong internal service compute resource rejected before any request is
  received or provider call occurs;
- successful temporary mapping cleanup proven by later platform-domain revoke.

The existing full unit/analyzer/generator/negative/admission/determinism/property
suite and pinned HybridCPU neutral integration remain the code-state gate.

### Slice-5 non-claims

This composition does **not** prove direct external use of a borrowed caller
mapping, cross-owner DSC1 submission, zero-copy, executable accelerator custody,
HybridCPU output visibility, provider-side DMA conflict enforcement or global
coherence. The Host provider remains `ModelOnly`, and HybridCPU-v2 remains
unchanged.

## Goal

Expose one narrow compute contour through semantic Sing capabilities, ownership
IPC and trustworthy completion without leaking lanes/opcodes/runtime internals.
A real HybridCPU-backed Phase-7 completion still requires the missing neutral
executable facade.

## Authority composition target for executable support

A future executable operation may proceed only when every independent layer is
live:

```text
caller/service compute authority
AND current source/destination memory authority
AND exact local generations
AND live neutral platform domain/mapping authority
AND provider feature at Executable level
AND exact accepted-work identity
AND provider custody/visibility requirements
AND trustworthy terminal publication
```

Intent, authority, evidence and published state remain distinct.

The current local Host path satisfies only the model/local terms. Its bounded
staging adaptation must not be used as evidence for the missing external terms.

## Memory rules

- immutable input remains read authority at the public boundary;
- mutable destination is exclusive MOVE authority;
- direct borrowed external execution requires a separately proven external
  read-grant/custody contract; current Slice 5 deliberately copies instead;
- CPU-visible output cannot publish before trustworthy terminal closure and any
  required executable acquire/visibility semantics;
- failed/cancelled work settles custody before local authority return;
- no global coherence premise;
- ownership transfer does not promise zero-copy.

## Feature truthfulness

Providers must distinguish `ModelOnly`, `Executable`, unavailable and denied.
Host lifecycle behavior is not HybridCPU hardware execution. DSC2, queues,
coherent overlap and arithmetic/reduction remain unsupported in this service
until separately scoped and proven.

## HybridCPU-v2 changes expected

Only a stable exported semantic facade if an existing neutral interface cannot
provide exact submit/completion/cancel/custody and visibility semantics. Internal
ISE descriptors, lanes/opcodes and compiler types are not an acceptable OS
contract.

Do not add new opcodes, DSC2, universal accelerator protocol, global coherence
or compiler lowering solely to close this phase.

## Acceptance criteria

Phase 7 is complete only when one **real neutral HybridCPU executable** compute
operation consumes/produces Sing-authorized memory with exact authority,
visibility, terminal completion and teardown/revocation proof through a stable
external facade.

Slices 1–5 close the complete **local Host `ModelOnly` vertical path**, including
typed SIP ownership ingress and service→platform→response composition. They do
not satisfy the external executable criterion. That remaining boundary is
isolated in `EXT-HCPU-005`.

## Next sequential slice — externally blocked

There is no justified local Add/Mul/Fma/Reduce or queue expansion after Slice 5.
The next Phase-7 closure slice is the external neutral executable DSC1 Copy
binding described by `EXT-HCPU-005`:

```text
stable neutral executable DSC1 Copy feature discovery
-> exact neutral source/destination custody
-> submit + bounded accepted-work identity
-> observe/cancel/drain
-> output CPU-visibility/acquire proof
-> close-before-rebind/reclaim
-> same typed ComputeService publication boundary
```

Until such a real facade exists, the correct repository state is to keep
HybridCPU compute `ExternalBlocked` and move no further local operation-set
breadth into Phase 7.

## Do not do

- no raw lane 6/lane 7 API;
- no raw MicroOp/opcode construction in SIPs;
- no universal GPU/accelerator ABI;
- no implicit coherent shared buffers;
- no provider/HybridCPU token as capability;
- no cross-owner provider authority invented to avoid bounded staging;
- no Add/Mul/Fma/Reduce/DSC2/queue expansion while executable Copy is externally
  blocked.
