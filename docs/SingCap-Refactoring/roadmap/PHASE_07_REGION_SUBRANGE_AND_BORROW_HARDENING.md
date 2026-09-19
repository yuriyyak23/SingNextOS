# Phase 07 — Region Subrange and Borrow Hardening

## Goal

Add fine-grained bounded memory authority by extending `RegionAuthority`, `OwnedBuffer<T>` and existing lease/view types.


## Baseline source anchors

- SingNextOS `a67eea1aafc72054d22f1586b62c6883cdc71681`
- HybridCPU-v2 `794c4a53494f503855ac8cf209efab23fde083b2`
- Technical specification: `../SINGCAP_M_TECHNICAL_SPEC.md`


## Non-negotiable rule

Do not create a second `RegionCapabilityAuthority` owning RegionId/generation/owner state. Any helper operates on the same `RegionAuthority.RegionRecord` / use ledger.

The audited baseline `RegionAuthority` has no internal synchronization domain while mutating its catalog, identity counters and records. P07 must not assume caller serialization.

## Work

### 1. Range projection

Add exact region-use/subrange derivation with checked byte arithmetic and access mode.

### 2. Explicit read/write lexical views

Evolve `BorrowedSpan<T>` toward explicit shapes such as:

```text
ReadBorrow<T>  -> ReadOnlySpan<T>
WriteBorrow<T> -> Span<T>
```

The exact public names are flexible. Authority resides in the active Region use record, not the `ref struct` alone.

### 3. Async lease

Reuse/evolve `BorrowLease<T>` for heap-safe async lifetime. Never store Span across await. Re-materialize a span on stack after validating the lease.

### 4. Conflict matrix

Define overlap and mode rules for read, exclusive write, staged output, direct coherent write, device-private and read-mostly modes.

### 5. Epoch semantics

Document and implement when `MutationEpoch` advances. Do not use it as a substitute for RegionGeneration.

### 6. Reuse/quarantine/zeroization

Tie external ambiguous completion to quarantine. Add zeroization when confidentiality domain changes before reuse.

### 7. Region concurrency and linearization

Document one linearization domain for every Region transition. The selected v1 design must protect both the mutable Region catalog/identity allocators and individual record state. A preferred implementation is a short catalog gate plus per-record (or striped) gates; a validated atomic state machine is acceptable if it proves equivalent semantics.

For multi-region operations, acquire record domains in deterministic `(RegionId, Generation)` order or use the Authority Composition reservation/revalidation protocol. Do not hold Region gates across provider calls, callbacks, blocking waits or user code. Independently prove that unrelated Regions can progress concurrently; a lone per-record lock without catalog safety and an undocumented global caller lock are both insufficient.

## Primary paths

```text
contracts/SingPlus.Contracts/Regions.cs
src/Runtime/SingPlus.Runtime/Regions/RegionAuthority.cs
src/Runtime/SingPlus.Runtime/Regions/RuntimeKernel.Regions.cs
src/Sip/SingPlus.Sip/Regions/OwnedBuffer.cs
src/Sip/SingPlus.Sip/Regions/BorrowLease.cs
src/Sip/SingPlus.Sip/Regions/OwnedRegion.cs
src/Runtime/SingPlus.Runtime/ExternalOperations/*
tests/SingPlus.Tests/Regions or Runtime region tests
```

## PR slices

- **P07-1:** checked range/subrange internal use records.
- **P07-2:** read/write lexical views + conflict policy.
- **P07-3:** async lease revalidation API.
- **P07-4:** generation/epoch/reuse/zeroization semantics.
- **P07-5:** external ambiguity quarantine integration.
- **P07-6:** catalog/record synchronization, deterministic multi-region ordering and lock/provider audit.

## Tests

- negative offset/length and checked overflow;
- type-size/alignment mismatch;
- overlapping read/read allowed according to policy;
- write/read and write/write conflicts;
- stale RegionGeneration/BorrowGeneration/MutationEpoch;
- double MOVE with barriers => one winner;
- Span cannot be boxed/captured across await;
- lease invalidation after close/reclaim;
- pool reuse rejects stale token and clears confidential bytes;
- provider cancellation ambiguity prevents reuse.
- allocate/resolve/reclaim races preserve catalog integrity and non-wrapping identity;
- MOVE/borrow/use/reclaim transitions have exactly one winner at their documented linearization point;
- unrelated Region operations make concurrent progress;
- multi-region reverse-order attempts do not deadlock;
- no provider call occurs under a Region authority lock.

## Performance gate

Benchmark:

```text
borrow acquire
Span loop after acquire
move
async lease acquire/revalidate
```

No capability/Region-table lookup may be inserted into each element access.

## Exit criteria

Fine-grained sharing is a projection of the existing Region lifecycle, every Region mutation has an executable-tested synchronization/linearization domain, and temporal safety is qualified independently from CLR physical object lifetime.
