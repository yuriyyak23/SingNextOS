# SipJob Normative Invariants

This document defines the invariants that every `SipJob` implementation, generator, analyzer, runtime path, cache, scheduler integration, test fixture, and qualification claim must satisfy.

Keywords **MUST**, **MUST NOT**, **SHOULD**, and **MAY** are normative.

## SJOB-001 — No new authority universe

`SipJob`, `SipJobPlan`, `SipJobHandle`, `JobRunFrame`, stage/edge descriptors, plan digests, graph verifier state, binding caches, worker-local state, completion summaries, and scheduling hints MUST NOT become independent authority ledgers.

Authority remains exclusively with existing owners, including:

- `CapabilityAuthority`;
- `ProcessRegistry`;
- `EndpointSessionRegistry`;
- `RegionAuthority`;
- `SealedObjectAuthority`;
- `ExternalOperationAuthority`;
- existing invocation/response/publication owners;
- existing platform/provider legality and admission owners.

Possession of a Job handle or plan MUST NOT make an otherwise-denied operation executable.

**Required proof:** execute an admitted plan after revoking each underlying authority class in isolation; execution MUST fail closed at the current owner without modifying the cached plan into an authority-bearing object.

## SJOB-002 — ManagedCap Job boundaries contain only closed admitted values

Raw mutable CLR references MUST NOT cross a ManagedCap SIP/Job boundary.

Forbidden edge/frame values include:

- service implementation instances;
- arbitrary mutable reference graphs;
- delegates, delegate targets, closures;
- `IServiceProvider` or service locators;
- capability-bearing ambient objects;
- generic `object`/reflection containers capable of carrying unadmitted references;
- mutable framework objects whose aliasing semantics are not represented by the SIP schema;
- service-created `Task`, `ValueTask`, custom awaitable, or async-state-machine object.

Generated copied-value projections MUST preserve ordinary SIP isolation. Shared runtime placement does not authorize passing the caller's mutable reference when ordinary SIP would create an isolated projection/copy.

TCB-private dispatch state MAY hold implementation references if it is unreachable from ManagedCap state and never placed into a Job descriptor/frame/edge.

**Required proof:** generator/admission negative fixtures for every forbidden reference form, including async capture of `this`, mutable closures, delegate fields, generic object containers, and mutable static roots.

## SJOB-003 — Every fused stage enters through an operation-specific generated sentry

A fused stage MUST NOT call a service implementation method directly.

The operation-specific generated sentry/static thunk MUST remain the trusted compartment transition and MUST consume the same admitted contract/schema metadata used by the ordinary SIP path.

Runtime dynamic IL generation, runtime source compilation, unrestricted reflection dispatch, and unqualified delegate injection are prohibited on the ManagedCap path.

**Required proof:** static/generated-manifest test demonstrating a fixed operation→thunk mapping plus a runtime negative test that no direct implementation invocation is reachable from the Job executor.

## SJOB-004 — Fusion may erase transport work, not authoritative semantics

A fused path MAY remove:

- intermediate `ChannelEnvelope`/response materialization;
- queue enqueue/dequeue;
- transport-only waiter/TCS allocation;
- transport-only scheduler wakeups;
- redundant discovery when a live binding is safely cached.

It MUST NOT remove or collapse:

- capability/effect admission;
- exact generation validation;
- session lifecycle semantics;
- sealing validation;
- Region ownership/use transitions;
- protocol transitions;
- external-effect lifecycle;
- required copy-isolation semantics;
- observable invocation/cancellation transitions;
- publication/release gates.

Semantic equivalence is defined over the ordered authoritative transition trace, not only the final result.

**Required proof:** differential trace comparison between ordinary SIP and fused execution after filtering a fixed whitelist of transport-only events.

## SJOB-005 — Every authority requirement names an existing authority source

Each stage authority requirement MUST identify an existing authority source class and the exact live identity needed to validate it.

The Job executor MUST NOT infer authority from:

- plan possession;
- prior stage success;
- cache hit;
- runtime co-location;
- service implementation identity;
- worker identity;
- HybridCPU lane/provider metadata.

An edge may carry `PreviousStageDeclaredReturn` only when the originating SIP contract explicitly returns transferable authority/evidence in a supported closed form.

**Required proof:** forge or mutate each authority-source descriptor independently; verifier or runtime admission MUST reject the plan/execution.

## SJOB-006 — Cache stores where/how to validate, never `authorized=true`

A verification or binding cache MUST NOT cache authorization success as executable authority.

Every run MUST perform live revalidation required by the corresponding owner.

Cache identity MUST be realm/incarnation safe. The binding key, where applicable, MUST include or be cryptographically/deterministically bound to all semantics that can invalidate execution, including:

```text
authority/runtime realm
process/service incarnation
session generation
capability lineage/reference and revocation/resource generation
seal generation
contract/schema/thunk digest
admission-policy/toolchain tuple
Job plan digest
provider generation/contract version only for stages that depend on it
```

A field may be omitted only if executable evidence proves that an existing opaque reference already binds it non-reusably.

**Required proof:** restart/ABA/replay suite with reused numeric IDs and stale Job handles.

## SJOB-007 — Region ownership/use is linear on every terminal path

`RegionAuthority` remains the sole memory ownership/use truth.

For each Region-bearing edge, the Job MUST preserve the complete sequence of authoritative transitions that ordinary SIP would perform. Matching only the final owner is insufficient.

A fused MOVE MUST NOT:

- double-transfer;
- skip an ordinary intermediate owner state;
- create an implicit inverse MOVE on failure;
- reuse a stale generation;
- reclaim before all dependent uses close;
- hide shared mutable state;
- treat a cached owner field as authoritative.

BORROW lifetime MUST dominate every consumer and every async/parallel continuation that can rematerialize a valid view.

**Required proof:** property/race tests covering success, exception, cancel-before-stage, cancel-during-stage, service restart, reclaim race, branch failure, join failure, and provider ambiguity.

## SJOB-008 — SipJob is not an ACID transaction

A Job MUST NOT claim atomic rollback of arbitrary service/private state or committed external effects.

The runtime MUST distinguish:

- reversible prepare state;
- committed authority consumption;
- private service mutation;
- Region transfer/use settlement;
- provider submission/completion;
- publication;
- release.

Irreversible private service mutation is a segment commit/barrier condition because later operations may observe it even before an external response is published.

**Required proof:** a stage mutates persistent private state, a later stage faults/cancels, and both ordinary and fused executions preserve the mutation according to the same admissible ordering.

## SJOB-009 — Composed admission separates reversible prepare from consumptive commit

The normative sequence is:

```text
resolve non-authoritative identities
prepare/probe reversible participants
pin/reserve reversible participants
final live revalidate
commit consumptive/non-compensatable participants
execute outside authority locks
settle
release/compensate only what its owner defines as reversible
```

A current operation-authority acquisition that consumes quota or one-shot state during acquisition MUST be treated as a commit linearization, not as a reversible prepare lease.

Reverse-order compensation MUST NOT invent rollback semantics that the authority owner does not provide.

If a fused segment needs multiple independently fallible non-compensatable commits without owner-provided reservations that make the remaining commit sequence safe, the segment MUST materialize/fallback rather than synthesize a Job-local transaction manager.

**Required proof:** one-shot/quota races, partial prepare failure, session close, seal close, Region reclaim, and two-concurrent-Jobs tests.

## SJOB-010 — Async state never smuggles implementation graphs across the boundary

An async stage MAY be qualified only when:

- no stack-only view survives across an await;
- all leases/pins that must survive are explicit heap-safe TCB-owned handles;
- service-created awaitables/state machines stay inside the generated sentry/compartment;
- `JobRunFrame` stores only TCB-owned correlation/completion state and admitted closed projections;
- cancellation and resumption revalidate all state whose owner requires revalidation.

JIT and NativeAOT async contours require separate executable evidence unless the qualification artifact explicitly proves both.

**Required proof:** incomplete-await fixtures capturing `this`, mutable state, delegates, and service dependencies; inspect generated metadata/IL/AOT artifacts and runtime reachability.

## SJOB-011 — Fusion barriers are explicit and conservative

The planner MUST materialize or terminate a fused segment when required by any of these classes:

- external effect whose lifecycle must be independently represented;
- externally observable publication;
- ownership settlement boundary;
- async/provider wait whose state cannot remain TCB-private under the qualified contour;
- cross-runtime boundary;
- independent cancellation/invocation observability;
- NativeIsolated boundary;
- confidential/secure domain boundary;
- irreversible private service mutation when later execution could falsely imply rollback;
- any unknown descriptor/barrier version;
- any authority sequence that cannot be composed without creating a second transaction/ledger.

Protocol transitions are not automatically materialization barriers, but they MUST linearize at the same semantic point and MUST NOT be deferred merely because transport is fused.

**Required proof:** one negative test per barrier class and a fail-closed test for unknown barrier values.

## SJOB-012 — Ordinary SIP is the semantic and conformance oracle

Every qualified Job contour MUST have an ordinary-SIP differential path using the same contracts and authority inputs.

The oracle MUST compare, as applicable:

- result/error/cancellation class;
- ordered semantic transition trace;
- protocol state;
- Region owner/generation/use state;
- capability/quota/one-shot state;
- invocation/publication state;
- externally visible private-state consequences;
- release/cleanup state.

Fallback MUST use ordinary SIP or reject. It MUST NOT silently weaken checks to preserve performance.

## SJOB-013 — Dynamic composition selects only precompiled admitted thunks

ManagedCap dynamic composition MAY choose among a finite set of precompiled, manifest-bound, admission-qualified stage thunks.

It MUST NOT introduce:

- dynamic IL generation;
- runtime code compilation;
- arbitrary reflection invocation;
- caller-supplied delegates;
- service-locator-based implementation discovery.

The thunk identity and schema/contract digest MUST participate in plan verification and cache identity.

**Required proof:** malformed/unknown thunk IDs and mismatched digests reject before service code.

## SJOB-014 — Protocol state remains independently authoritative

Protocol-state machines remain owned by their existing runtime owner.

Fusion MUST NOT:

- infer a transition from stage order;
- delay a transition to Job completion when ordinary SIP linearizes it earlier;
- allow a parallel external invocation to observe an impossible protocol state;
- cache a protocol state as authority in the Job frame.

**Required proof:** competing ordinary invocation is injected before/after each fused protocol transition; the set of legal outcomes must match the ordinary-SIP ordering model.

## SJOB-015 — Completion, visibility, publication, settlement, and release are distinct

The following MUST remain separable states:

```text
stage execution completed
provider execution completed
memory/device visibility established
result publication committed
Region ownership settled
external-operation lifecycle settled
pins/leases released
```

No Job-level `Completed` bit may substitute for these owners.

Provider completion evidence or CPU certificate MUST NOT become a SingNext capability.

**Required proof:** delay each state boundary independently in test instrumentation and verify that the next state does not become visible early.

## SJOB-016 — Qualification and claims are contour-specific

A qualified contour MUST NOT automatically qualify an adjacent contour.

The following dimensions are independently gated where applicable:

- synchronous vs async;
- copied value vs BORROW vs MOVE;
- linear vs read-only DAG vs parallel DAG;
- same-runtime vs split-runtime;
- no external effect vs external-effect stage;
- JIT vs NativeAOT;
- generic managed execution vs specific HybridCPU/provider acceleration;
- normal managed domain vs NativeIsolated/confidential domain.

Claims MUST identify the exact source/toolchain/policy/provider tuple and the exact enabled feature gates.

No theoretical complexity claim, model-only property, or presence of metadata may be presented as measured or runtime-enforced evidence.
