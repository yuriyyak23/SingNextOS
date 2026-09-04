# Phase 7 — Semantic compute and accelerator provider

## Status

**In progress — local Slices 1–3 complete; HybridCPU executable path remains
`ExternalBlocked`.** Depends on Phases 1–6 and corresponds to `EXT-HCPU-005`.

## Delivered Slice 1 — bounded DSC1 Copy host model

The first local vertical slice deliberately implements only contiguous
`UInt8`, `AllOrNone` `Copy`, with a 1 MiB maximum operation size and disjoint
source/destination owned regions. `Add`, `Mul`, `Fma`, `Reduce`, DSC2, queues,
scatter/gather and overlapping copies are not part of contract v1.

The implemented authority intersection is:

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

`CapabilityId`, local DSC1 submission ID/generation, provider operation
ID/generation, domain lease and mapping leases remain separate types. The
capability never enters the provider request, and the local continuation never
contains the provider operation identity.

The default Host provider advertises this contour only as `ModelOnly`. The
`RuntimeKernel` snapshots the exact source range into private bounded staging,
holds internal reservations that reject subsequent owner access through the
two `OwnedBuffer<byte>` APIs, and publishes the staged bytes to the destination
only after an exact
`Closed + Completed` provider receipt. `Closed + Cancelled` releases the
reservations without modifying output. Pending, denied, stale, malformed or
faulted completion cannot publish output; malformed/faulted completion pins
the mappings and domain against unsafe reclaim.

The Host provider admits and closes lifecycle state only and never reads or
writes region contents; the CPU reference copy belongs to `RuntimeKernel`.
`ModelOnly` is therefore defined to have no external memory effect. A provider
denial or malformed accepted identity may release these local buffer
reservations, but its domain/mappings remain quarantined because exact external
authority closure was not proved. This is not accelerator execution, zero-copy,
a coherence claim or ISE evidence. Whole-region host mappings remain the
existing local/runtime-admission model; bounded DSC1 ranges add no claim that
the Host implements executable exact-slice mapping or accelerator cache
maintenance.

The reservation is not a revocable managed-memory capability: a raw `Span<T>`
obtained before submission remains a usable alias. The model guarantees only
that new access through `OwnedBuffer<T>` is rejected and that the submitted
source bytes come from the private pre-submit snapshot. Executable custody must
use a stronger external contract and remains blocked.

Compute-capability revocation, explicit mapping closure and process teardown
cancel/drain the exact operation before mapping/domain closure and local region
reclaim. Observation and cancellation remain available for already-authorized
work after local capability revocation or while process teardown is draining.

The SingNextOS HybridCPU provider does not advertise `Dsc1BulkCompute` and does
not implement `IPlatformDsc1ComputeProvider`. Submission therefore fails with
`PlatformUnsupported` before any compute-provider call or host fallback. The
live neutral domain and mappings are not poisoned by this ordinary unsupported
result.

Tests cover successful bounded publication, pending isolation, cancellation,
capability/resource/right rejection before provider invocation, owner/range/
access/generation failures, forged local continuations, provider denial,
malformed submit/completion/cancellation fault pinning, cancellation drain
retry, concurrent single-winner finalization, submit/revoke serialization,
the pre-acquired managed-alias limitation, capability-revoke/teardown ordering,
forbidden topology vocabulary and HybridCPU fail-closed conformance.

## Delivered Slice 2 — generation-bound DSC1 observation wakeup

DSC1 now reuses the same `KernelEventEndpoint` already used for IRQ and model
DMA completion. No accelerator-specific event object, queue or provider token
was added. The only new public shape is an overload of the existing terminal
observation:

```text
ObservePlatformDsc1Copy(
  exact ProcessHandle,
  exact PlatformDsc1CopySubmission,
  exact KernelEventEndpoint)
    -> exact PlatformDsc1CopyReceipt
       + one committed KernelEventClass.Completion notification
```

The runtime validates the exact live process generation, endpoint owner and
generation, and complete local submission before provider observation. It then
reserves the endpoint's single slot invisibly, observes and validates the typed
provider terminal outcome, settles the local payload, and only then commits the
event. `Completed` publishes the staged output and releases both buffer
reservations before a waiter can wake. An observed `Cancelled` outcome leaves
output unchanged, releases the same reservations, and may publish the same
observation wakeup. The event source contains only local submission ID and
generation.

`Pending`, provider denial, malformed/faulted evidence and all stale or forged
inputs publish no event. Every failure before terminal settlement rolls back
the exact invisible reservation. A full endpoint backpressures before the
provider call, so a terminal provider observation is not consumed when local
notification cannot be retained. Replay cannot invoke the provider or publish
a second event. The exact staged pin and the DSC1 gate make a post-settlement
`CommitExact` failure an internal invariant loss; that path faults without
claiming delivery and retains fail-closed endpoint state rather than reclaiming
through an unaccounted publication.

The event is an optional, observation-driven wakeup, not an autonomous provider
push, compute capability, completion receipt, provider closure proof,
memory-visibility result or reclaim authority. Cancelling a
`WaitForKernelEventAsync` waiter or closing its endpoint does not cancel DSC1 or
return the buffers. Direct `CancelPlatformDsc1Copy` remains endpoint-free and
does not promise an event. During process exit the event-bearing overload is
rejected, while existing endpoint-free observation/cancellation continues the
exact provider drain. Provider closure and local payload release still precede
mapping/domain closure and region reclaim.

`KernelEvent` intentionally does not encode `Completed` versus `Cancelled`.
The caller that performs observation must propagate the returned typed
`PlatformDsc1CopyReceipt` to any consumer that needs the authoritative outcome;
the event alone only says that this exact local observation committed.

The Host `ModelOnly` implementation retains the existing coarse DSC1 payload
serialization while it calls the provider. A stalled provider observation can
therefore delay the start of another DSC1 operation or process teardown. This
is fail-closed for publication and reclaim, but is not a prompt-revocation or
non-blocking-provider guarantee; a future executable provider needs a bounded
per-operation in-flight protocol rather than relying on this model lock.

Tests cover pending rollback and retry, successful output-before-wakeup,
observed cancellation, occupied endpoint backpressure, exact at-most-once
delivery, stale/closed/foreign endpoint and stale/forged submission rejection,
provider denial and malformed completion, waiter cancellation, endpoint-close
races, process-exit drain, recycled process generations and absence of early
reclaim.

## Delivered Slice 3 — conservative DMA↔DSC1 mapping-use interlock

`RuntimeKernel` now serializes local DMA/DSC1 admission and release through its
private platform-memory-use gate. The bridge independently derives conflict
state from the existing DMA and DSC1 lifecycle ledgers under their private
gates; it creates no parallel authority or reservation registry. This two-layer
interlock introduces no public contract, provider method, capability, platform
grant or event type. A use is keyed by the complete local
`PlatformRegionMapping` identity, including its generation. Normal accepted DMA
uses are owned by an exact local operation record; ambiguous or invariant-fault
submit paths retain a grant-scoped fault pin that resolves the same exact
mapping. DSC1 uses are owned by the exact local submission record.

The first rule is deliberately conservative:

```text
active or fail-closed DMA submit-path use of mapping M
  conflicts with
active or fail-closed DSC1 source or destination use of mapping M
```

This is whole-mapping exclusivity across the two mechanisms. It applies even
when both sides would read, and even when their requested byte subranges do not
overlap. The local bridge does not claim cache-line conflict analysis,
range-granular engine compatibility or cross-engine coherence. Independently
authorized distinct mappings may have overlapping accepted lifetimes in the
same platform domain binding. Their submission admission still passes through
the coarse local gate. Because current `RegionAuthority` permits only one live
platform mapping per owned region, that disjoint case also denotes distinct
owned regions rather than two aliases of one region.

Admission validates the complete local authority first. The coarse admission
gate and bridge ledger locks remain held across the provider submit and exact
local record publication, so the other mechanism cannot enter that mapping in
the provisional window. An ordinary provider denial returns without publishing
an active lifecycle record. A successful acceptance retains the use through
that record. If malformed success, provider fault or a throw leaves acceptance
ambiguous, the exact use remains pinned through a trustworthy operation record
or exact grant-scoped fault pin, or the containing platform domain is
quarantined when exact operation identity cannot be retained. Forged/stale
mapping, grant, submission or process identities cannot release another
operation's use.

DMA grant creation and visibility preparation alone are not active mapping
uses. A DMA use starts when submission is accepted; ambiguous provider
acceptance or a fail-closed submit-path invariant fault instead creates the
exact grant-scoped fault pin. Exact completion proof alone does not release an
accepted use: `DeviceReadsMemory` must traverse the exact post-completion
no-acquire finalization, while write-capable directions must also finish the
required direction-aware CPU acquire. Only the successful post-completion
visibility transition releases the exact DMA use. Pending, malformed or faulted
completion/visibility state retains it.

DSC1 holds both source and destination uses until exact `Completed` or
`Cancelled` provider closure is validated, completed output is published or
teardown output is discarded, both local buffer leases are released, and the
bridge commits the exact local-reservation release. Pending observation,
ordinary observation/cancellation denial, malformed terminal state, provider
fault or throw retains both mapping uses and therefore blocks conflicting DMA
and unsafe mapping/domain reclaim.

This interlock proves only local cross-mechanism admission and exact pin
lifetime. A prepared-but-unsubmitted DMA cycle is intentionally not a mapping
use, and current `OwnedBuffer`/pre-acquired managed aliases carry no mutation
epoch that invalidates old prepare evidence after an intervening CPU or DSC1
write. Executable reuse therefore still needs an external visibility/custody
contract that binds preparation to the current mutation epoch or requires a
fresh prepare. Slice 3 is not that contract and makes no hardware-DMA,
accelerator, IOMMU or coherence claim.

The current local model/test contour assumes provider calls made inside these
coarse private gates are bounded and do not wait on a cross-thread callback
that re-enters the same authority path. DSC1 lifecycle is exercised by the Host
`ModelOnly` provider; the combined DMA↔DSC1 cases use a faithful test provider,
not a Host executable-DMA path. A stalled or re-entrant provider can delay even
disjoint admission. Before any executable provider is classified as supported,
it needs a bounded provisional per-operation reservation and reconciliation
protocol that does not depend on holding this coarse gate across an unbounded
external call.

Tests cover DMA-first and DSC1-first conflicts before the second provider call,
read/read and non-overlapping-subrange rejection on the same mapping, permitted
overlap of accepted uses on distinct mappings, rollback after ordinary submit
denial,
retention through pending and completion-before-visibility, exact release after
DMA post-completion visibility and DSC1 completed/cancelled settlement, exact
fault pinning or containing-domain quarantine, stale/forged identity rejection
and mapping-close/ownership-transfer rejection while authority remains pinned.

## Goal

Expose one **narrow, code-confirmed HybridCPU compute contour** through semantic Sing capabilities, owned regions and completion without leaking lanes/opcodes/runtime internals.

## Recommended first contour

Prefer **DSC1 bulk compute** for the first integration because it exercises the same authority boundaries as DMA while remaining semantically small:

```text
Copy
Add
Mul
Fma
Reduce
```

MatrixTile v1 is a valid alternative when numeric/layout contracts are more important. Scoped L7-SDC is a later useful device-style contour.

Do not start by designing a universal GPU/accelerator API.

## Provider shape

A compute provider should expose semantic profiles and operations:

```text
QueryComputeProfiles()

ComputeSubmission SubmitDsc1(
    DomainLease,
    ComputeCapabilityContext,
    input region slices,
    output region slices,
    operation,
    numeric/layout profile)

Wait/Poll/Cancel
CompletionReceipt
```

For MatrixTile, use typed shape/layout/numeric descriptors. For L7-SDC, use command families rather than raw lane-7 opcodes.

## Authority composition

A submission proceeds only when all are live:

```text
caller/service compute capability
owned/borrowed exact input/output authority
platform domain lease
provider compute feature at Executable level
provider memory grants/visibility requirements
operation-specific HybridCPU runtime admission
```

Provider tokens/handles are evidence/continuation keys, not Sing capabilities.

## Memory rules

- immutable/read inputs may use bounded read grants;
- mutable outputs should use exclusive ownership or a single-writer grant;
- CPU cannot observe output until completion + acquire;
- failed/cancelled operations must define whether output is unchanged, undefined-but-owned, or partially written;
- choose an all-or-none semantic where the HybridCPU contour actually guarantees it; do not strengthen weaker backend semantics.

## Feature truthfulness

The provider must distinguish:

- DSC1 supported;
- DSC2/queue/async overlap unsupported if not active;
- MatrixTile profile supported/unsupported;
- L7 command family supported/unsupported;
- coherent async memory not available unless explicitly proven.

A host fallback can implement the same semantic interface, but provider identity/evidence must make clear that the operation was host-executed.

## SingNextOS API layering

Possible source-familiar API:

```text
System.Compute-like library
  -> generated typed ComputeService SIP
  -> ComputeCapability + OwnedRegion/OwnedBuffer
  -> privileged platform compute bridge
```

The kernel ABI sees only region/grant/submission/completion primitives. It does not contain matrix algorithms or accelerator command vocabularies.

## HybridCPU-v2 changes expected

Only a stable exported semantic facade if current internal runtime paths are not safe/stable for external integration. Reuse existing code-confirmed MatrixTile/DSC1/L7 authority and completion mechanisms.

Do **not** add new opcodes, DSC2, universal accelerator protocol, global coherence or compiler lowering solely for this phase.

## Tests

- missing local compute capability prevents provider call;
- wrong region owner/generation/range denied;
- unsupported profile reports unsupported, not host fallback masquerading as hardware;
- stale provider token cannot query/cancel a newer submission;
- cancellation triggers drain/revoke before output ownership returns;
- provider malformed completion cannot publish SIP success;
- no generated SIP manifest contains physical lane IDs/opcodes;
- host and HybridCPU providers pass the same authority-negative conformance suite.

## Acceptance criteria

Phase 7 is complete when one real HybridCPU compute operation can consume/produce Sing-owned regions with explicit memory visibility and trustworthy completion while keeping the source API semantic and platform-neutral.

Slices 1–3 do not satisfy that full-phase criterion. The missing stable neutral
HybridCPU submission/completion/cancel facade and executable output-visibility/
custody proof remain isolated in `EXT-HCPU-005`.

Before any provider can expose executable DMA and DSC1, it must independently
enforce or compose equivalent mapping-use, visibility and drain rules. The
current Host provider has no executable DMA and the current HybridCPU provider
has no DSC1 surface, so Slices 1–3 neither implement nor claim a combined
hardware path.

The next local Phase-7 pool should carry the same bounded Copy authority through
a generated typed `ComputeService` SIP ingress. It should support exactly the
source-read and destination-exclusive authorities needed by this contour, with
atomic validation/rollback and ownership return, rather than widening the
operation set or adding a generic accelerator protocol. This is a SingNextOS
contract task and does not remove the executable external blocker.

## Do not do

- no raw lane 6/lane 7 API;
- no raw MicroOp/opcode construction in SIPs;
- no universal GPU API;
- no implicit coherent shared buffers;
- no “accelerator token == capability” shortcut.
