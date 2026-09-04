# Phase 7 — Semantic compute and accelerator provider

## Status

**In progress — local Slices 1–2 complete; HybridCPU executable path remains
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

Slices 1–2 do not satisfy that full-phase criterion. The missing stable neutral
HybridCPU submission/completion/cancel facade and executable output-visibility/
custody proof remain isolated in `EXT-HCPU-005`.

Before any provider can expose executable DMA and DSC1 over the same mapping,
the next local authority-composition pool should define their cross-mechanism
mapping-use conflict/interlock rules. It must reject conflicting active use,
permit independently authorized disjoint mappings, and release or retain each
pin only after exact completion/cancel/fault disposition. The current Host
provider has no executable DMA and the current HybridCPU provider has no DSC1
surface, so Slices 1–2 neither implement nor claim a combined hardware path.

## Do not do

- no raw lane 6/lane 7 API;
- no raw MicroOp/opcode construction in SIPs;
- no universal GPU API;
- no implicit coherent shared buffers;
- no “accelerator token == capability” shortcut.
