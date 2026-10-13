# SipJob Feature Gates and Promotion Rules

All `SipJob` functionality is fail-closed and default-off until the corresponding gate reaches the required evidence level.

## 1. Evidence levels

| Level | Meaning |
|---|---|
| `ModelOnly` | semantics documented; no executable enforcement claim |
| `StaticAdmission` | generator/analyzer/admission tooling rejects unsupported forms before execution |
| `RuntimeEnforced` | live runtime owner checks and negative tests enforce the contour |
| `QualifiedManaged` | runtime enforcement plus differential/race/property/performance qualification on an exact managed-runtime tuple |
| `ProductionCandidate` | `QualifiedManaged` plus deployment/operability/rollback/claim criteria for the exact production-shaped contour |

Promotion is monotonic only for the exact contour. Evidence does not transfer sideways to a different gate or runtime/provider tuple.

## 2. Global promotion requirements

A gate MUST NOT advance beyond `ModelOnly` unless:

1. P14-0 exact tuple is pinned.
2. Its descriptor schema is canonical and digest-bound.
3. Unsupported/unknown forms fail closed.
4. Ordinary SIP remains available as a semantic oracle/fallback.
5. Negative tests demonstrate that a Job handle/cache hit cannot bypass live owners.
6. No service/provider/user code executes under authority locks.
7. Semantic transition tracing exists for every authoritative transition touched by the gate.
8. Cleanup/terminal-path tests show no leaked pins/leases/Region uses.
9. Claims identify the gate and exact tuple.

## 3. Gate table

| Gate | Default | Maximum before prerequisite | Promotion prerequisites |
|---|---|---|---|
| `FG-JOB-LINEAR` | OFF | `ModelOnly` | P14-0/1; 2-stage same-runtime sync closed-value plan verification |
| `FG-DIRECT-SENTRY` | OFF | `ModelOnly` | P14-2 generated operation-specific thunk; semantic-trace equivalence; no raw-ref escape |
| `FG-REGION-BORROW` | OFF | `ModelOnly` | P14-3 borrow lifetime/use-state proof on all terminal paths |
| `FG-REGION-MOVE` | OFF | `ModelOnly` | exact ordinary ownership-transition sequence; generation/stale-handle differential proof |
| `FG-MULTI-SESSION-SEGMENT` | OFF | `ModelOnly` | P14-4 reversible prepare vs consumptive commit; one-shot/quota/session-close race suite |
| `FG-BARRIER-MODEL` | OFF | `ModelOnly` | P14-5A all barrier classes and unknown-value fail-closed behavior |
| `FG-ASYNC-STAGE` | OFF | `ModelOnly` | P14-5B hidden-awaitable/reference proof, resumption/cancel semantics, runtime-specific qualification |
| `FG-READONLY-DAG` | OFF | `ModelOnly` | P14-5C no mutable fan-out, deterministic join, branch settlement, lifetime domination |
| `FG-DAG-PARALLEL` | OFF | `ModelOnly` | `FG-READONLY-DAG` qualified plus parallel race/contention/scheduler evidence |
| `FG-EXTERNAL-EFFECT-STAGE` | OFF | `ModelOnly` | existing `ExternalOperationAuthority` lifecycle preserved; provider-specific completion/publication tests |
| `FG-DYNAMIC-PLAN-BIND` | OFF | `ModelOnly` | P14-6 finite precompiled catalog; exact schema/thunk binding; no dynamic IL/reflection dispatch |
| `FG-PLAN-CACHE` | OFF | `ModelOnly` | realm/incarnation-safe keys; live revalidation; restart/ABA/replay tests |
| `FG-SPLIT-RUNTIME` | OFF | `ModelOnly` | ordinary transport boundary materialization; no claim of fused cross-runtime raw state |
| `FG-HYBRIDCPU-HINTS` | OFF | `ModelOnly` | semantic-only hints; compatible provider-neutral contract or explicit versioned contract |
| `FG-HYBRIDCPU-ACCEL` | OFF | `ModelOnly` | exact provider contour has independent platform admission, legality, completion/visibility evidence |
| `FG-NATIVEAOT-JOB` | OFF | `ModelOnly` | NativeAOT-specific generator/runtime/admission/async evidence |
| `FG-NATIVEISOLATED-FUSION` | OFF | `ModelOnly` | FutureGated; separate isolation and transition model required |
| `FG-MUTABLE-DAG` | OFF | `ModelOnly` | FutureGated; separate shared-mutable ownership/conflict proof required |
| `FG-CONFIDENTIAL-DOMAIN-FUSION` | OFF | `ModelOnly` | FutureGated; separate confidential-domain authority/evidence model required |

## 4. Gate dependencies

```text
FG-JOB-LINEAR
   |
   +--> FG-DIRECT-SENTRY
           |
           +--> FG-REGION-BORROW
           +--> FG-REGION-MOVE
           +--> FG-MULTI-SESSION-SEGMENT
                   |
                   +--> FG-BARRIER-MODEL
                           |
                           +--> FG-ASYNC-STAGE
                           +--> FG-READONLY-DAG
                           |       |
                           |       +--> FG-DAG-PARALLEL
                           +--> FG-EXTERNAL-EFFECT-STAGE

FG-DYNAMIC-PLAN-BIND --> FG-PLAN-CACHE
FG-HYBRIDCPU-HINTS --> FG-HYBRIDCPU-ACCEL
```

Dependencies establish prerequisite mechanisms, not inherited qualification.

## 5. Region-specific gating

### `FG-REGION-BORROW`

Required evidence:

- borrow acquisition/release remains owned by `RegionAuthority`;
- no per-element capability lookup after a valid view is acquired;
- no view rematerialization after lease/use closure;
- async/parallel consumers do not outlive the shared lease;
- cancel/fault/restart/reclaim races leave no leaked use state.

### `FG-REGION-MOVE`

Required evidence additionally includes:

- ordered owner sequence equal to ordinary SIP semantics;
- same number of authoritative `Transfer` linearizations unless a separately specified contract proves otherwise;
- expected generation advance after every transfer;
- stale handles become stale at equivalent logical points;
- no implicit inverse transfer on downstream failure.

A test that checks only final owner is insufficient.

## 6. Composed-admission gating

`FG-MULTI-SESSION-SEGMENT` MUST remain disabled until the runtime distinguishes:

```text
reversible prepare/pin/reserve
from
consumptive/non-compensatable commit
```

An existing operation-authority acquisition that consumes quota/one-shot state is classified as commit.

If all-stage atomic pre-admission would require reserving such authority before execution, the gate MUST remain limited to just-in-time stage commit unless `CapabilityAuthority` itself exposes and qualifies an owner-controlled reservation/commit primitive. No Job-local shadow reservation is permitted.

## 7. Async gating

`FG-ASYNC-STAGE` does not inherit synchronous qualification.

Qualification MUST show:

- no `Span`/stack-only state survives an await;
- no service-created `Task`/`ValueTask`/state machine is stored as Job edge/frame state;
- TCB-private continuation state cannot be reached from ManagedCap code;
- cancellation is linearized against service/provider completion;
- resumed execution revalidates required session/Region/provider state;
- JIT and NativeAOT are separately identified.

## 8. DAG gating

### `FG-READONLY-DAG`

Only immutable/copied values and qualified shared-read BORROW fan-out are allowed.

Forbidden:

- MOVE fan-out;
- shared writable Region state;
- implicit branch-to-branch communication;
- non-deterministic join policy without a separately specified observable semantics;
- branch-local release of a shared resource still needed by a sibling.

### `FG-DAG-PARALLEL`

Parallel scheduling requires additional evidence for:

- branch cancel/fault propagation;
- deterministic join and settlement;
- lock/contention behavior;
- cache/false-sharing effects;
- worker migration/scheduler transitions;
- no authority settlement before all relevant branch outcomes are known.

## 9. HybridCPU/provider gates

`FG-HYBRIDCPU-HINTS` permits only semantic hints. A hint MUST NOT be treated as permission and MUST NOT name physical lanes/opcodes/private handles.

If an internal hint cannot map to the currently qualified provider-neutral contract, the runtime MUST ignore/fallback or use an explicitly versioned new provider contract. Silent semantic widening is prohibited.

`FG-HYBRIDCPU-ACCEL` requires both:

```text
LocalAllowed
AND
PlatformAllowed
```

and evidence for the exact provider contour. Qualification of one stream/lane/accelerator contour does not qualify MatrixTile, L7, SecureCompute, virtualization, or any other contour.

## 10. FutureGated contours

The following stay `ModelOnly` and disabled until separately specified:

- shared-mutable DAG;
- NativeIsolated fusion;
- confidential/SecureCompute domain fusion;
- direct exposure of provider-private topology/handles;
- cross-runtime raw-reference fusion;
- arbitrary runtime-generated code.

They must not be enabled through configuration-only changes to existing gates.
