# EXT-HCPU-005

**Status:** External Blocked

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

HybridCPU-backed `System.Compute`/accelerator services only. Local SIP contracts, ownership model, host-side compute providers and API design remain unblocked.

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