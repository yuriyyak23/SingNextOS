# Cross-project Contracts and Non-goals

Status: complete for the provider-neutral SingNextOS contract boundary.

Evidence: `12_PHASE12_IMPLEMENTATION_EVIDENCE.md`.

Phase-16 revalidation confirms that ambiguous provider unavailability is not a
closure receipt, the Type-2 ABI identifies an opaque fabric binding by both ID
and generation, and the new `NotAccepted` status preserves all pre-existing
numeric status values. Effect-creating CXL contracts now reserve `NotAccepted`
for proven zero-effect rejection and require result-based failure reporting.
No HybridCPU-v2, compiler/lowering, ISA, lane, replay or external-package source
was changed.

## Purpose

Keep this roadmap strictly inside SingNextOS while documenting what later HybridCPU-v2 or compiler work may depend on.

## Hard scope exclusions

This roadmap and its implementation PRs must not:

- edit `HybridCPU-v2`;
- edit the HybridCPU compiler/lowering pipeline;
- add/modify ISA instructions;
- change lane6 `DmaStreamCompute` semantics;
- change lane7 `L7-SDC` encoding/execution semantics;
- modify HybridCPU legality/GuardPlane/replay certificate internals;
- add `Remote Lane`;
- make SingNextOS emulate HybridCPU replay;
- make SingNextOS emulate absent CXL hardware coherence/security/fabric enforcement.

## Contracts SingNextOS may expose for later HybridCPU integration

### External operation admission receipt

Should identify, opaquely:

- operation ID/generation;
- admitted device/service identity;
- region-use set;
- effect class;
- publication policy;
- cancellation support.

It must not expose raw CXL transport IDs.

### Completion/visibility/publication receipt

Should allow an external runtime to distinguish:

```text
Submitted
DeviceComplete
Visible
Published
Released
Failed/Stale
```

### External-effect class

The external runtime may consume:

```text
StagedReversibleUntilPublish
SnapshotOrIdempotenceRequired
IrreversibleBarrier
```

How those classes map to HybridCPU replay tokens/certificates/barriers is not specified here.

### Memory/provider capability query

An external runtime may ask semantic questions:

- is the region device-readable/writable under the current binding?
- is coherent access available for these agents?
- is staging required?
- what visibility action is required?
- is secure-compute admission ready?

It must not ask for raw HDM/DPA/fabric route identities.

## Compiler boundary

The compiler should eventually express semantic intent and constraints, not CXL topology. No compiler task belongs to this roadmap. If future lowering needs a new semantic property, first add a provider-neutral runtime/OS contract and only then open a separate compiler design review.

## HybridCPU-v2 boundary

The future HybridCPU-v2 roadmap may choose to consume SingNextOS services through L7-SDC for accelerator commands and ordinary memory semantics for CXL-backed regions. SingNextOS does not prescribe new ISA carriers here.

## Versioning

Any cross-project API introduced by SingNextOS must be versioned independently of CXL link/spec revision. CXL 4.0 support should normally extend provider capability records rather than break the external operation API.

## Review checklist

Reject a SingNextOS CXL change if it requires any of the following to be meaningful:

- a new CPU instruction before the OS contract works;
- a compiler-emitted CXL port/decoder identity;
- a HybridCPU replay certificate as memory authority;
- a HybridCPU lane identity inside `RegionAuthority`;
- a CXL hardware token pretending to be a Sing capability.

The correct dependency direction is:

```text
SingNextOS authority + lifecycle + provider semantics
        -> stable external contract
        -> later HybridCPU-v2 integration
        -> later compiler integration if needed
```
