# EXT-HCPU-005

**Status:** Local DSC1 v1 / Host `ModelOnly`; HybridCPU executable binding
`ExternalBlocked`

## Local boundary now implemented

SingNextOS now has a narrow DSC1 Copy v1 authority/lifecycle contract:

- separate local `Dsc1ComputeCapability` with `Execute` authority;
- disjoint `OwnedBuffer<byte>` source/destination mappings and exact bounded
  equal-length ranges;
- bridge-private provider operation identity and public local continuation;
- exact typed completion/cancellation disposition composed with the generic
  `PlatformCompletionReceipt` closure proof;
- private bounded staging and runtime reservations that block subsequent
  `OwnedBuffer` API access, so model output is not published before verified
  `Closed + Completed`;
- cancellation/capability revoke/process teardown closes the operation before
  mapping/domain/local reclaim;
- Host advertises `Dsc1BulkCompute` v1 only as `ModelOnly`;
- the HybridCPU provider reports v0/`Unavailable` and performs no implicit Host
  fallback.

This local reference operation is neither ISE execution nor evidence that a
HybridCPU accelerator consumed or produced the mapped regions.

The Host provider models only submit/completion/cancel lifecycle and has no
region-content effect; `RuntimeKernel` performs the local CPU-staged reference
copy. A managed `Span<T>` acquired before reservation cannot be revoked, so
this slice does not prove exclusive CPU custody. That limitation is explicit
and covered by a boundary test rather than hidden behind a stronger claim.

## Exact external blocker

The audited neutral HybridCPU integration has no stable semantic DSC1 facade
that accepts neutral domain/mapping authority and returns exact submit,
completion, cancellation/drain and output-visibility evidence. Internal ISE
DSC1 descriptors, lane selection, provider tokens or compiler types are not an
acceptable substitute. In addition, an executable asynchronous provider must
prove output CPU-access custody and post-completion visibility for a reusable
mapping; the Host-only staging rule does not prove that hardware boundary.

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

## SingNextOS component blocked

HybridCPU-backed `System.Compute`/accelerator execution and its reusable
output-visibility boundary only. The local capability/ownership model and Host
reference provider are no longer blocked. A generated product ComputeService
SIP remains a separate SingNextOS task because the current single ownership-
payload request shape cannot yet carry both source and destination authority.

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
