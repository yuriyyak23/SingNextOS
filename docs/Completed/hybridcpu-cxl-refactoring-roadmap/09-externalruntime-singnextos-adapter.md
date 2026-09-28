# Phase 9 — ExternalRuntime SingNextOS Adapter

## Goal

Make `HybridCPU_ExternalRuntime` the only normal HybridCPU layer that speaks the SingNextOS external-operation contract, keeping OS/provider/CXL details out of `HybridCPU_ISE` and compiler core logic.

## Adapter responsibilities

Implement an adapter that can:

- build provider-neutral admission requests from HybridCPU descriptors;
- pass opaque region/service handles established by host/runtime integration;
- receive admission/effect/visibility/generation receipts;
- submit exactly once;
- correlate completion with exact operation/descriptor identity;
- request visibility/publication/release transitions as required;
- surface reset/reconfiguration/stale state;
- translate OS failures into HybridCPU external-operation status without inventing CPU legality.

## Project boundary

Preferred dependency direction:

```text
HybridCPU_ISE
   -> HybridCPU_ExternalRuntime.Contracts
   -> HybridCPU_ExternalRuntime
   -> SingNextOS adapter/transport
```

Avoid a direct dependency from `HybridCPU_ISE` on SingNextOS implementation assemblies.

The compiler may depend on shared stable semantic contract definitions where appropriate, but not on live OS/runtime provider code.

## Transport abstraction

Keep the adapter transport-neutral so test and deployment environments can use:

- in-process test adapter;
- IPC/RPC boundary;
- hypercall/VM service boundary;
- native SingNextOS service bridge.

Transport failure is distinct from provider rejection and device failure.

## Handle discipline

HybridCPU stores opaque handles only. Handles must be:

- scoped to an owner/domain/session;
- generation-bound;
- non-forgeable through ordinary descriptor bytes;
- invalidated on OS-side revoke/reset/reconfiguration as required.

## Capability query

Expose semantic queries such as:

- external accelerator service available;
- DMA read/write available;
- coherent host/device access available;
- staged publication supported;
- direct output supported;
- cancellation supported;
- effect class and visibility requirements.

Do not expose raw CXL link/fabric topology as normal runtime policy input.

## Error taxonomy

Distinguish at minimum:

- transport unavailable;
- contract version mismatch;
- admission rejected;
- stale generation;
- submit rejected;
- device fault/reset;
- visibility failure;
- publication rejected;
- cancellation unsupported/failed;
- release failure requiring cleanup/fault handling.

## Tests

`HybridCPU_ExternalRuntime.Tests` should include deterministic fake adapter traces for every lifecycle transition and invalid transition, plus contract version negotiation and reconnection tests.

## Exit criteria

HybridCPU ISE and compiler tests can run entirely against fake contracts, while one adapter integration suite proves the same lifecycle against SingNextOS without leaking CXL implementation details upward.