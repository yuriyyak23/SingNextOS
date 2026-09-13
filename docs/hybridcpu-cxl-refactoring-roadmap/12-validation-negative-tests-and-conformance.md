# Phase 12 — Validation, Negative Tests, and Conformance

## Goal

Turn the architecture invariants into executable tests across HybridCPU ISE, ExternalRuntime, and the compiler.

## Test layers

### Compiler unit tests

Cover:

- semantic intent construction/propagation;
- lane6/lane7 lowering;
- typed-slot capacity and alias conflicts;
- fallback policy encoding;
- no CXL topology in IR/descriptors;
- contract version emission.

### ISE unit/model tests

Cover:

- legality and GuardPlane independence from OS admission;
- L7 token lifecycle;
- DSC lifecycle;
- replay effect handling;
- commit/visibility/publication separation;
- fences and cancellation;
- stale generation behavior.

### ExternalRuntime contract tests

Cover:

- lifecycle transition legality;
- operation/descriptor correlation;
- version negotiation;
- duplicate/late completion;
- transport failure;
- opaque generation invalidation.

### Cross-project integration tests

Against a fake SingNextOS adapter first, then a real SingNextOS service test environment:

```text
compile -> decode -> CPU legality -> OS admission -> submit -> complete -> visible -> publish -> release
```

The same test vectors should support non-CXL and CXL-backed providers where semantics match.

## Mandatory negative matrix

Every implementation series must include tests for:

- CPU legality allows but OS rejects;
- OS admits but GuardPlane becomes stale;
- mutation/generation drift before submit;
- generation drift after submit;
- device reset before and after DeviceComplete;
- visibility failure;
- duplicate submit attempt during replay;
- late completion from old generation;
- wrong operation/descriptor completion;
- duplicate publication;
- unsupported cancellation;
- cancellation/completion race;
- direct coherent write attempted without explicit capability/effect proof;
- compiler attempts to encode raw CXL topology;
- CXL Type-3 memory used through ordinary loads/stores without a CXL-specific ISA path;
- provider unavailable with required vs preferred external intent.

## Replay conformance

Prove that:

- replay certificates do not grant external permission;
- `ReplayToken` cannot cause duplicate external side effects;
- submitted non-idempotent operations become wait/cancel/barrier cases rather than blind reissue;
- published external effects are represented in replay boundary policy.

## Authority conformance

Prove that none of these alone authorize an operation:

- compiler metadata;
- a structural certificate;
- a replay certificate;
- CXL device discovery;
- link up;
- coherent-access capability;
- CXL.mem range availability;
- prior successful admission from an old generation.

## Encapsulation conformance

Fail tests/build checks if normal compiler/ISE public structures expose provider-private identifiers such as:

- HDM decoder ID;
- DPA;
- CXL switch/port route;
- FM binding ID;
- raw CCI/flit/TLP details;
- low-level IOMMU hardware identifiers not required by an explicit platform-materialization boundary.

## CI gates

Recommended gates:

1. compiler semantic/lowering suite;
2. ISE safety/replay suite;
3. L7-SDC suite;
4. DSC suite;
5. ExternalRuntime lifecycle suite;
6. fake SingNextOS integration suite;
7. real SingNextOS/QEMU CXL integration suite when available.

## Exit criteria

No phase is considered complete until its negative tests demonstrate fail-closed behavior for stale authority, duplicate effects, reset/reconfiguration, and publication separation.