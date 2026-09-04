# EXT-HCPU-005

**Status:** Local DSC1 v1 + DMA mapping-use interlock / Host `ModelOnly`;
HybridCPU executable binding `ExternalBlocked`

## Local boundary now implemented

SingNextOS now has a narrow DSC1 Copy v1 authority/lifecycle contract:

- separate local `Dsc1ComputeCapability` with `Execute` authority;
- disjoint `OwnedBuffer<byte>` source/destination mappings and exact bounded
  equal-length ranges;
- bridge-private provider operation identity and public local continuation;
- exact typed completion/cancellation disposition composed with the generic
  `PlatformCompletionReceipt` closure proof;
- optional observation-driven, generation-bound terminal wakeup through the
  existing `KernelEventEndpoint`, committed only after local
  output/reservations settle;
- private bounded staging and runtime reservations that block subsequent
  `OwnedBuffer` API access, so model output is not published before verified
  `Closed + Completed`;
- cancellation/capability revoke/process teardown closes the operation before
  mapping/domain/local reclaim;
- private RuntimeKernel admission serialization plus bridge lifecycle-ledger
  checks prevent accepted or ambiguously accepted DSC1 work from sharing either
  exact mapping with active DMA, and prevent accepted/ambiguous DMA from
  entering a mapping held by DSC1;
- Host advertises `Dsc1BulkCompute` v1 only as `ModelOnly`;
- the HybridCPU provider reports v0/`Unavailable` and performs no implicit Host
  fallback.

This local reference operation is neither ISE execution nor evidence that a
HybridCPU accelerator consumed or produced the mapped regions.

The event-bearing observation is also local-only and is not an autonomous
provider-pushed completion. It validates the exact process, endpoint and
submission generations before provider observation,
reserves endpoint capacity invisibly, and commits a `Completion` notification
only after a verified terminal result has settled local output and custody.
Pending/error paths roll that reservation back. Waiter or endpoint cancellation
does not cancel DSC1, and process teardown continues through the endpoint-free
provider drain before mapping/domain/local reclaim. The notification is not a
provider receipt, compute authority, output-visibility proof or hardware event.
It does not encode `Completed` versus `Cancelled`; the observer must propagate
the returned typed receipt wherever that authoritative outcome is required.

The Host `ModelOnly` DSC1 path keeps provider observation inside the private
RuntimeKernel platform-memory-use gate and bridge DSC1 ledger gate. A stalled
model provider can delay the start of local teardown, although no output, event
or region reclaim can cross that blocked observation. This is not a prompt-
revocation guarantee. A future executable binding must supply a bounded per-
operation in-flight/drain protocol rather than depend on this serialization.

The Host provider models only submit/completion/cancel lifecycle and has no
region-content effect; `RuntimeKernel` performs the local CPU-staged reference
copy. A managed `Span<T>` acquired before reservation cannot be revoked, so
this slice does not prove exclusive CPU custody. That limitation is explicit
and covered by a boundary test rather than hidden behind a stronger claim.

The cross-mechanism interlock uses the complete local mapping identity, not
byte-range overlap. Same-mapping read/read and non-overlapping subranges are
therefore rejected conservatively. Accepted lifetimes on distinct independently
authorized mappings may overlap, although submission admission passes through
the coarse local gate. DMA completion alone does not release a use: required
post-completion visibility must finish. DSC1 releases only after terminal
settlement, local publication/discard, both buffer-lease releases and the exact
local release commit. Ordinary pre-accept denial rolls back; pending observation
and any malformed, faulted, thrown or ambiguous external state retain the exact
uses where they remain identifiable, or quarantine the containing platform
domain when exact operation identity cannot be retained. This policy is local
and is neither accelerator execution nor provider-side conflict enforcement.

Provider submit calls in the local model/test contour also execute inside coarse
private gates. Host supplies only the DSC1 `ModelOnly` half; combined DMA↔DSC1
behavior uses a faithful test provider and is not Host DMA evidence. Calls are
assumed bounded and must not wait on cross-thread re-entry. A future executable
provider needs provisional per-operation reservation/reconciliation instead of
treating this locking shape as a liveness guarantee.

## Exact external blocker

The audited neutral HybridCPU integration has no stable semantic DSC1 facade
that accepts neutral domain/mapping authority and returns exact submit,
completion, cancellation/drain and output-visibility evidence. Internal ISE
DSC1 descriptors, lane selection, provider tokens or compiler types are not an
acceptable substitute. In addition, an executable asynchronous provider must
prove output CPU-access custody and post-completion visibility for a reusable
mapping; the Host-only staging rule does not prove that hardware boundary.
The executable facade must also compose or enforce cross-engine mapping-use and
drain policy rather than trusting only the local bridge.

A prepared-but-unsubmitted DMA cycle is outside the new active-use interlock.
Current owned buffers and pre-acquired managed aliases expose no mutation epoch
that would make older prepare evidence stale after a later CPU or DSC1 write.
Executable DMA/compute reuse of a mapping therefore additionally needs a
mutation/visibility epoch or mandatory fresh prepare rule. This remains an
external memory-visibility boundary shared with `EXT-HCPU-004`, not a capability
that completion metadata can supply.

Per project direction, this requirement records those gaps only. This
iteration makes no change to HybridCPU ISE or `HybridCPU_Compiler_v2`.

## Required external capability

The external HybridCPU platform integration must provide semantic bindings, where the platform already exposes them, for the scoped code-confirmed compute contours relevant to SingNextOS:

- MatrixTile v1 load/store/compute;
- DmaStreamCompute DSC1 bulk operations;
- scoped L7-SDC accelerator commands.

SingNextOS must not need to use raw physical lane selection or raw opcode construction as its OS authority API.

## Why SingNextOS needs it

HybridCPU code confirms useful specialized execution planes, but SingNextOS public/kernel policy should remain expressed in capabilities, owned regions and semantic operations. A stable external adapter is required to use these features without coupling SIP contracts to HybridCPU internal MicroOp/descriptor/runtime types.

## Existing interface expected

An already existing or externally supplied platform interface that can expose a subset of:

### MatrixTile

- supported numeric/layout profiles;
- load from an admitted memory region;
- supported compute operations;
- store to an admitted memory region;
- typed fault/completion status.

### DSC1

- Copy/Add/Mul/Fma/Reduce where supported;
- exact range/type validation;
- all-or-none completion;
- cancellation/fault/commit outcome;
- truthful statement that DSC2/queue/coherent async modes are unsupported if they are not active.

### L7-SDC

- query capabilities;
- submit;
- poll/wait;
- cancel;
- fence;
- status;
- scoped memory-footprint binding and commit result.

## Minimal reproduction

1. Bind a SingNextOS domain and owned regions using the domain/memory integration contracts.
2. Discover which of MatrixTile/DSC1/L7 services the external provider reports as available.
3. Invoke one supported operation through a semantic adapter without manually selecting a physical lane.
4. Verify a missing local SingNextOS capability prevents the platform call.
5. Verify an external denial or malformed result does not mutate local region ownership/protocol state.
6. Verify unsupported/future-gated modes report unavailable rather than silently falling back to a different authority contour.
7. Verify the external provider rejects or drains DMA and compute that target
   the same mapping lifetime, and that terminal closure/visibility releases only
   the exact operation's use.

## SingNextOS component blocked

HybridCPU-backed `System.Compute`/accelerator execution and its reusable
output-visibility boundary only. The local capability/ownership model and Host
reference provider are no longer blocked. A generated product ComputeService
SIP remains a separate SingNextOS task because the current single ownership-
payload request shape cannot yet carry both source and destination authority.
That generated typed SIP ingress is the next local Phase-7 pool and does not
depend on changing HybridCPU ISE or compiler code.

## Explicit non-request

This requirement does **not** ask for:

- new HybridCPU opcodes;
- DSC2 implementation;
- universal external accelerator protocol;
- global CPU/device coherence;
- compiler/backend changes;
- SingNextOS control of HybridCPU lane allocation.

## Fallback/mock used

Host reference providers may implement the same semantic interfaces for tests. Provider identity must be explicit so host fallback is never reported as ISE hardware execution evidence.
