# P14-8 — Security Qualification, Performance Characterization, and Claim Closure

## 1. Goal

Close each enabled `SipJob` contour with executable security/conformance evidence, statistically meaningful performance data, supply-chain binding, and claims no broader than the tested tuple.

Performance never substitutes for security qualification.

## 2. Qualification order

For each contour:

```text
1. static closed-world admission
2. runtime negative tests
3. deterministic race/property tests
4. ordinary-vs-fused semantic-trace differential tests
5. leak/cleanup checks
6. performance characterization
7. supply-chain/manifest closure
8. claim review
9. optional ProductionCandidate promotion
```

Any failure in steps 1-5 blocks promotion regardless of performance.

## 3. Required comparison paths

Every benchmark report must identify at least:

### A. Direct-call diagnostic baseline

Plain in-process direct call without SIP security/transport semantics.

Purpose: lower-bound diagnostic only. It is not a valid replacement for SIP/Job and must not be used to claim that security checks are overhead that should be removed.

### B. Ordinary full SIP

Normal session/request/reply path including existing envelope/queue/waiter/publication semantics.

### C. Fused SipJob

Exact enabled Job gates under test.

### D. Forced-materialization Job

Same Job plan but barriers force ordinary intermediate SIP boundaries. Helps separate graph/planner overhead from fusion savings.

### E. Split-runtime fallback

Where applicable, ordinary cross-runtime execution using the current transport boundary.

### F. Provider-accelerated contour

Only for P14-7, and always reported separately from Job fusion.

## 4. Workload matrix

### 4.1 Stage count

```text
2 / 4 / 8 / 16 stages
```

At least one test must use a tiny stage body so transport/security/runtime overhead is visible, and one must use a meaningful compute body to show amortization.

### 4.2 Value payloads

Closed copied/value forms:

```text
empty/control
small scalar/record
4 KiB
64 KiB
bounded maximum admitted copied value
```

Record security-required copied bytes separately from transport metadata bytes.

### 4.3 Region payloads

For separately qualified BORROW/MOVE gates:

```text
4 KiB
64 KiB
1 MiB
16 MiB or another >1 MiB point supported by the test environment
```

### 4.4 DAG

For read-only DAG:

```text
fan-out 2 / 4 / 8
serial DAG executor
parallel DAG executor where qualified
```

### 4.5 Workers

```text
1 / 2 / 4 / 8 / 16 / 32 workers
```

Do not run worker counts that exceed the environment without recording CPU/topology context; oversubscription is a separate datum, not a silent default.

### 4.6 Fault/cancel/lifecycle paths

Benchmark and validate at least:

```text
success
service fault
cancel before stage
cancel at sentry/admission boundary
cancel during async stage when qualified
fault after committed MOVE
branch failure/join failure
service restart/stale binding
provider deny
provider ambiguous completion where qualified
```

## 5. Mandatory metrics

### Latency/throughput

```text
median (p50)
p95
p99
throughput
```

Max may be reported but is not a substitute for tail percentiles.

### Allocation/GC

```text
allocations/op
allocated bytes/op
GC bytes/op (or harness-equivalent managed allocation/collection byte metric)
GC collections by generation where meaningful
GC pause/time evidence where material
```

### Transport/runtime counters

```text
ChannelEnvelope count/op
ResponseRegistry/ResponseEnvelope materialization count/op
enqueue/dequeue count/op
waiter/TaskCompletionSource count/op
materialized invocation count/op
scheduler suspend/resume/wakeup transitions/op
worker migration/work-steal count for parallel DAG
```

### Authority/security counters

```text
capability lookups/op
capability enumeration/scans/op where the ordinary path performs them
capability admission/commit operations/op
session lookups/pins/revalidations/op
seal validation/pin operations/op
Region use acquisitions/releases/op
Region transfers/op
Region generation advances/op
protocol transitions/op
publication operations/op
```

### Cache/discovery counters

```text
service discovery lookups/op
verification-cache hits/misses
live-binding-cache hits/misses
stale rebinds/op
```

### Contention

```text
lock acquisitions/op
aggregate lock wait
p95/p99 lock wait where measurable
contention/failure/retry counts
```

### Data movement

```text
security-required copied bytes/op
transport metadata bytes/op
provider staging bytes/op where applicable
```

## 6. Expected fast-path savings

The implementation may reasonably target elimination/reduction of:

- intermediate `ChannelEnvelope`/response materialization;
- enqueue/dequeue;
- intermediate waiter/TCS allocation;
- scheduler transitions caused solely by the materialized intermediate boundary;
- repeated service discovery after a safe live binding cache hit;
- repeated static manifest/graph verification after a safe verification-cache hit.

The implementation MUST NOT claim elimination of:

- capability/effect admission;
- session/generation checks required by owner semantics;
- Region ownership/use transitions;
- sealing;
- protocol transitions;
- isolation-required value copy/projection;
- external-operation lifecycle;
- publication/visibility gates.

A reduced authority lookup count must be explained. Reusing an owner-issued pin/lease can remove redundant lookups only when its existing semantics guarantee the required property; caching `authorized=true` is prohibited.

## 7. Security differential matrix

For every promoted gate compare ordinary vs fused state after each test:

```text
public result/error/cancel class
authoritative semantic event order
capability quota/one-shot/revocation state
session state/generation
protocol state
Region owner/generation/use state
seal state
invocation/publication state
external-operation state
remaining pins/leases/cache roots
```

A final-state-only comparison is insufficient for Region/protocol/capability sequencing.

## 8. Race/property qualification

Required deterministic injection around:

```text
session pin/revalidation
capability commit
seal revalidation
Region use/transfer
protocol transition
implementation entry/exit
external submit
provider completion/visibility
publication
release
```

Run concurrency stress in addition to deterministic hooks, not instead of them.

## 9. Repetition/statistics

Benchmark artifacts must record:

- warmup policy;
- iteration count;
- process/runtime configuration;
- GC mode;
- CPU count/topology relevant to workers;
- runtime mode JIT/NativeAOT;
- confidence/variance summary appropriate to the harness;
- exact binary/source tuple.

No fixed speedup threshold is required by architecture. Promotion should require that the claimed optimization is measured and repeatable for the stated workload, not that an arbitrary percentage is met.

A contour with no meaningful performance benefit may remain correct but should not be promoted/advertised as a useful fast path for that workload.

## 10. Performance interpretation rules

### Empty/control stages

Best case for exposing envelope/queue/waiter costs. Also most likely to show that security checks are a large fraction of remaining fused cost. Do not characterize those checks as removable overhead.

### Large BORROW/MOVE

Expected savings may plateau because data is already shared/moved without per-element capability lookup. Memory bandwidth, Region transition, cache locality, and provider staging may dominate.

### Copied values

A fused path may still need security-required copying. Report copied bytes so reduced envelope serialization is not mislabeled as zero-copy.

### Parallel DAG

Throughput gains must be considered together with p99 latency, contention, false sharing, scheduler transitions, and branch cleanup cost.

## 11. HybridCPU/provider reporting

For accelerated contours report two deltas:

```text
ordinary SIP -> fused managed Job
fused managed Job -> fused + qualified provider acceleration
```

Also report platform admission, provider staging, completion, visibility, and publication costs.

Do not merge these into one undifferentiated "SipJob speedup" number.

## 12. Claim levels

### `ModelOnly`

Allowed claim: architecture/model is specified.

### `StaticAdmission`

Allowed claim: unsupported forms are rejected by the exact generator/analyzer/admission tuple.

### `RuntimeEnforced`

Allowed claim: current runtime owners enforce the specified contour with negative/race evidence.

### `QualifiedManaged`

Allowed claim: exact managed contour passed semantic differential, race/property, cleanup, and performance characterization on the pinned tuple.

### `ProductionCandidate`

Requires additionally:

- production-shaped configuration;
- gate default/fallback policy;
- observability counters;
- operational rollback by gate disablement;
- no unresolved blocker/critical security finding;
- supply-chain artifact closure;
- claim text reviewed against exact enabled gates.

## 13. Prohibited claim inflation

Do not claim:

- `zero-copy` when copied-value isolation or provider staging remains;
- `O(1)` authority cost from theoretical reasoning alone;
- `transactional` or `atomic Job` semantics;
- hardware/secure/confidential enforcement from metadata/model-only support;
- all DAGs from read-only DAG qualification;
- async from synchronous qualification;
- NativeAOT from JIT qualification;
- HybridCPU acceleration from generic scheduling-hint support;
- production readiness from workstation microbenchmarks.

## 14. Qualification artifacts

Produce machine-readable artifacts analogous in discipline to existing security/performance qualification outputs, containing:

```text
exact tuple
feature-gate set
contour description
security test summary
semantic differential summary
race/property summary
performance summary
known exclusions/FutureGated features
claim level
```

Raw benchmark results should remain available for review, not only aggregated prose.

## 15. Rollback/fallback qualification

Before `ProductionCandidate`:

- disabling each gate restores ordinary SIP without data migration;
- stale Job handles after disablement cannot force fast-path execution;
- split/materialized fallback is tested;
- gate disablement during restart/deployment does not create an authority bypass;
- observability identifies whether a request executed fused or materialized without exposing sensitive authority data.

## 16. Final exit criteria

P14 reaches implementation closure only when every enabled contour has:

- exact source/toolchain/provider tuple;
- executable static/runtime/race/differential evidence;
- terminal-path cleanup proof;
- measured performance counters showing what was and was not removed;
- a claim no broader than its exact gate set;
- tested ordinary-SIP fallback;
- no implicit qualification of FutureGated contours.
