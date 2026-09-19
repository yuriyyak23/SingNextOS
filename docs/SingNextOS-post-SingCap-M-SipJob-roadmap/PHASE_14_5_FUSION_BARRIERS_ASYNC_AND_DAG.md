# P14-5 — Fusion Barriers, Async/Cancellation and Feature-Gated Read-Only DAG

## Goal

Introduce explicit points where fusion must stop/materialize, support safe suspension/resume, and add **read-only DAG fan-out only behind feature gates**.

## FusionBarrier model

At minimum support the following classes:

```text
ExternalEffect
Publication
OwnershipSettlement
AsyncProviderWait
CrossRuntime
UnqualifiedNative
ConfidentialDomain
IndependentCancellation
ExternallyObservableInvocation
```

The planner may fuse across a boundary only when no required barrier class is present and all required feature gates are enabled. Unknown/unsupported class fails closed.

## Inline vs materialized stages

### Inline stage

Allowed only when it has no independent externally visible invocation lifecycle. It may use lightweight `JobStageAttempt` evidence inside the trusted executor.

### Materialized stage

Required when a stage needs independent cancellation, observer-visible invocation, provider callback, response publication, durable correlation or other ordinary lifecycle semantics. It registers in the existing `EndpointSessionInvocationRegistry`/response lifecycle rather than a Job-private substitute.

## Async stage

`FG-ASYNC-STAGE` allows suspension only with heap-safe authoritative handles/leases. Rules:

```text
no Span/ref-like value across await
save JobRunId + StageId + exact lease/pin references/correlations
release locks before suspension
on resume revalidate current required lease/session/Region state
rematerialize Span on stack
continue or fail closed according to static policy
```

## Cancellation

A Job may bind one `CancellationScopeHandle`, but cancellation disposition remains stage/effect-specific. “Cancel Job” never fabricates cancellation of an admitted/irreversible provider effect. Join logic must settle all branch-owned borrows/uses before terminal publication.

## Read-only DAG feature gate

`FG-READONLY-DAG` is **not part of the initial linear release**. It may be enabled only after P14-3 and P14-4 are qualified.

Initial DAG restrictions:

```text
acyclic graph
bounded nodes/edges/fan-out/fan-in
fan-out edges are read-only Region uses/borrows or immutable bounded values
no shared mutable multi-writer edge
no MOVE to more than one successor
join stage has deterministic declared inputs
all branches share explicit cancellation/join settlement policy
```

Example:

```text
             -> Hash ---------
Input Region                  -> Join/Publish
             -> Classify -----
```

Both branches may obtain compatible read-only uses from the same Region only if the P07 conflict matrix admits them.

## Parallel DAG gate

`FG-DAG-PARALLEL` is separate from DAG correctness. First qualify DAG semantics with deterministic/single-worker scheduling. Parallel execution additionally requires race/contention evidence and must not change authority/ownership outcomes.

## Explicitly FutureGated

The following remain OFF beyond this roadmap's release target unless a later roadmap qualifies them:

```text
shared mutable DAG
multi-writer graph
implicit lock-sharing object graphs
confidential-domain fusion
NativeIsolated fusion without independent isolation proof
```

## PR slices

- P14-5.1: barrier classification in generated plan metadata.
- P14-5.2: materialization fallback at barriers.
- P14-5.3: async stage state machine and lease revalidation.
- P14-5.4: Job cancellation/join settlement.
- P14-5.5: read-only DAG verifier + deterministic executor behind `FG-READONLY-DAG`.
- P14-5.6: optional parallel read-only branches behind `FG-DAG-PARALLEL`.

## Tests

```text
unknown barrier => reject/materialize
independently cancellable stage => materialized lifecycle
Span cannot survive suspension
cancel before stage admission
cancel after local stage admission
provider cancellation ambiguity remains pinned/quarantined where applicable
read/read fan-out accepted
read/write or write/write fan-out rejected
branch failure closes its borrows and join settles remaining branches
parallel vs serial DAG yields same ownership/publication result
```

## Exit criteria

Fusion boundaries are explicit and auditable. Read-only DAG is still a separately switchable contour with its own evidence; mutable DAG remains FutureGated.
