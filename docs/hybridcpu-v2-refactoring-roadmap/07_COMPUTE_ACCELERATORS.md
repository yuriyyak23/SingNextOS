# Phase 7 — Semantic compute and accelerator provider

## Status

**Post-DMA stress slice.** Depends on Phases 1–6 and corresponds to `EXT-HCPU-005`.

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

## Do not do

- no raw lane 6/lane 7 API;
- no raw MicroOp/opcode construction in SIPs;
- no universal GPU API;
- no implicit coherent shared buffers;
- no “accelerator token == capability” shortcut.
