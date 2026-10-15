# P14-5 — Fusion Barriers, Async Stages, Read-Only DAG, and Parallel Execution

## 1. Goal

Define conservative segmentation boundaries and then add async/read-only DAG/parallel contours as independently gated extensions.

This phase is deliberately split into four qualification sub-phases:

```text
P14-5A  barrier classification and materialization
P14-5B  async stage contour
P14-5C  deterministic read-only DAG semantics
P14-5D  parallel DAG execution
```

No sub-phase inherits qualification from another except explicit prerequisites.

---

# P14-5A — Fusion barriers

## 2. Barrier principle

A `FusionBarrier` does not mean the Job ends. It means the next semantic boundary must be materialized or handled through the existing lifecycle owner rather than being erased as an internal fused edge.

A barrier is required whenever eliminating the ordinary boundary would erase independently meaningful state or would require a Job-local transaction/authority model.

## 3. Required barrier classes

### 3.1 `ExternalEffectBoundary`

Required before/around provider/device/network effects whose prepare/admit/submit/complete/visibility/release lifecycle is independently authoritative.

### 3.2 `PublicationBoundary`

Required when an intermediate result is externally observable or existing protocol semantics require response publication before subsequent work.

### 3.3 `OwnershipSettlementBoundary`

Required when ordinary SIP establishes an ownership state that must become independently authoritative before the next stage and cannot be represented solely as an internal continuation.

This does not permit collapsing Region transitions; P14-3 remains normative.

### 3.4 `AsyncWaitBoundary`

Required when the async wait cannot remain within a qualified TCB-private sentry continuation or when session/Region/provider lifecycle must be independently materialized during suspension.

### 3.5 `CrossRuntimeBoundary`

Always materialize through ordinary transport semantics. No raw-reference fusion across runtimes.

### 3.6 `IndependentCancellationBoundary`

Required when the intermediate invocation has an independently observable cancellation identity/lifecycle.

### 3.7 `ObservableInvocationBoundary`

Required when external code can observe, cancel, query, or depend on the invocation as a separate object/lifecycle.

### 3.8 `NativeIsolatedBoundary`

FutureGated. Materialize through the existing isolated transition.

### 3.9 `ConfidentialDomainBoundary`

FutureGated. Materialize through the existing secure/confidential-domain transition. No qualification inherited from managed fusion.

### 3.10 `IrreversiblePrivateMutationBoundary`

Required whenever continuing fusion would imply that a later failure rolls back a private service mutation that ordinary execution leaves committed.

A private mutation does not have to be externally published immediately to be irreversible; future requests may observe it.

### 3.11 `UnsupportedAuthorityCommitBoundary`

Required where P14-4 cannot safely compose multiple non-compensatable commits using existing owner semantics.

### 3.12 `UnknownBoundary`

Unknown/new barrier classes fail closed and materialize/reject. They are never treated as `None`.

## 4. Protocol transitions are not transport

A protocol transition is always authoritative but is not automatically a materialization barrier.

The direct path may keep execution fused only if it commits the protocol transition at the same semantic point and preserves the same outcomes for competing external invocations.

If an independently observable protocol action requires publication/materialization, the relevant barrier class applies.

## 5. Barrier planner tests

For every class above:

- construct an otherwise-fusible two-stage plan;
- insert exactly one barrier condition;
- assert planner produces the defined materialized segment split;
- assert no security check disappears across the split;
- assert unknown barrier version fails closed.

---

# P14-5B — Async stages

## 6. Async TCB boundary

An async service implementation commonly creates a compiler-generated state machine containing `this`, locals, dependencies, and mutable references. This state MUST remain inside the generated sentry/compartment.

The Job executor MUST NOT store the service-produced `Task`, `ValueTask`, custom awaitable, closure, or state-machine object as a Job edge value.

Allowed Job-side suspended state:

```text
opaque TCB-owned invocation correlation
opaque owner-issued pins/leases that are explicitly safe across suspension
closed projected input/output state
cancellation correlation
semantic stage ID
```

Forbidden:

```text
service implementation reference
async state machine reference
awaiter containing service-private graph
captured dependency/delegate
raw Region backing object
```

## 7. Async lease/pin rules

A stack-only view (`Span<T>`, ref struct, stack borrow proxy) MUST NOT survive across an await.

If a Region use must survive suspension:

- the authoritative heap-safe lease/use handle is explicit;
- view rematerialization revalidates lease state as required by existing Region semantics;
- cancellation/fault closes the use exactly once;
- service restart/session close behavior is defined and tested.

## 8. Async cancellation linearization

Required races:

```text
cancel before service starts
cancel just before await suspension
cancel while suspended
cancel concurrent with completion
completion before cancel publication
provider completion but visibility pending
session close while suspended
```

The result must match ordinary SIP's allowed outcome set. The Job must not publish success merely because an awaited task completed if publication/session/provider conditions are no longer satisfied.

## 9. Async JIT/NativeAOT qualification

JIT and NativeAOT are separately recorded gates/tuples. Qualification must include the generated state-machine/linkage behavior of the actual runtime mode.

---

# P14-5C — Deterministic read-only DAG

## 10. Scope

The first DAG contour is deliberately restrictive:

```text
acyclic graph
single logical Job entry
immutable/copied-value fan-out OR qualified read-only Region BORROW fan-out
no MOVE fan-out
no shared writable state
no branch-to-branch mutable communication
explicit deterministic join policy
bounded branch count
```

Shared-mutable DAG remains FutureGated.

## 11. Fan-out rules

### Copied/immutable value fan-out

Each branch receives a closed value whose aliasing semantics are immutable/value-like or separately projected according to schema.

### Read-only Region fan-out

All branches may share the same authoritative read-only use only when existing Region semantics permit it.

The shared lease lifetime dominates every branch capable of accessing the Region.

No branch can release the shared lease independently while siblings remain active.

## 12. Region conflict matrix

Initial DAG verifier:

| A | B | Result |
|---|---|---|
| read-only BORROW | read-only BORROW | allowed if same lifetime scope and Region rules permit |
| MOVE | any sibling use | reject |
| write/use mutation | sibling read | reject |
| write | write | reject |
| unknown use class | any | reject |

## 13. Branch cancellation/failure

The plan declares a closed join/cancellation policy ID. Examples may include all-success semantics, but arbitrary callback policies are not allowed.

For each branch outcome:

- branch-local reversible resources close once;
- shared resources remain until all dependent branches settle;
- committed private mutations are not rolled back;
- authority consumed by an already-entered branch is not refunded by Job code;
- unstarted branches do not consume their operation authority;
- output publication waits for the declared join condition.

## 14. Deterministic join

Join behavior MUST NOT depend on worker scheduling order.

The public fault/cancel/result selection rule must be deterministic from the declared policy and branch semantic outcomes, not from whichever branch happened to finish first unless "first completion" is itself an explicit separately qualified observable contract.

The initial gate SHOULD avoid first-completion/racing joins.

## 15. Lifetime domination proof

For every shared Region use or shared TCB resource:

```text
Acquire
  dominates all dependent branches
Join/branch-settlement
  post-dominates all dependent accesses
Release
  occurs after that post-dominator
```

Graph verification plus runtime property tests must agree on the same lifetime scope.

---

# P14-5D — Parallel DAG execution

## 16. Scope

Parallel execution is a scheduler optimization over already-qualified deterministic read-only DAG semantics.

It does not create additional authority and must not alter branch/join outcomes.

## 17. Worker/scheduler rules

- workers carry no ambient Job authority;
- a work item contains only stage ID + TCB-private correlation/closed state;
- worker migration does not change authority semantics;
- branch admission remains at the generated sentry/owner boundaries;
- no worker-local owner cache can substitute for live revalidation;
- scheduling hints are semantic only; physical HybridCPU placement remains P14-7/provider-owned.

## 18. False sharing/cache effects

Qualification measures, but does not change semantics based on:

- cache-line contention;
- false sharing in Job runtime bookkeeping;
- branch migration;
- work stealing;
- lock contention;
- GC pressure.

A performance issue may disable/restrict the parallel gate; it may not justify weaker isolation or ownership checks.

## 19. Executable proof matrix

For branch counts `2/4/8` and workers `1/2/4/8/16/32`, cover:

```text
all success
one branch fault
multiple faults
one cancel
cancel + fault
service restart in one branch
shared Region reclaim attempt
join failure path
```

Assertions:

- result/fault/cancel class independent of scheduling order;
- no authority consumed by never-started branch;
- no shared lease released early;
- no ownership settlement before required join state;
- no leaked pins/leases after partial completion;
- no hidden mutable branch communication.

## 20. PR decomposition

1. P14-5A barrier enum/planner/materialization tests;
2. P14-5B async sentry containment + cancellation tests;
3. P14-5C read-only DAG verifier + serial executor;
4. P14-5C property/lifetime qualification;
5. P14-5D parallel scheduler integration;
6. P14-5D contention/performance/race qualification.

Each sub-contour has its own gate and can be reverted independently.

## 21. Exit criteria

- all barrier classes fail closed and are tested;
- irreversible private mutation cannot be hidden behind fake rollback semantics;
- async state cannot escape the service compartment through Job state;
- read-only DAG has deterministic join/lifetime semantics;
- parallel execution does not alter authority or result ordering semantics;
- shared-mutable DAG remains disabled/FutureGated.
