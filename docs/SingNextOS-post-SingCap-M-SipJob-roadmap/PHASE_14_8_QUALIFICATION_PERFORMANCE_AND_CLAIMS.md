# P14-8 — Security, Concurrency, Performance and Claim Closure

## Goal

Release no SipJob contour until fused execution has adversarial, race, ownership, fallback and performance evidence against the ordinary SIP semantic reference.

## Required claim levels

Use the existing conservative vocabulary:

```text
ModelOnly
StaticAdmission
RuntimeEnforced
QualifiedManaged
ProductionCandidate
```

Each feature gate receives its own level. Qualification of `FG-JOB-LINEAR` does not raise `FG-READONLY-DAG`, async, provider, split-runtime or HybridCPU acceleration claims.

## Security matrix

At minimum:

```text
forged/replayed Job plan/binding handle
runtime/service/process restart stale binding
contract/message/thunk substitution
raw CLR/delegate/service-object injection
confused-deputy executor-owned capability attempt
capability ancestor revoke vs stage/segment admission
session close vs segment commit
seal close/service restart vs stage admission
Region MOVE/reclaim vs Job admission
one-shot/quota competition
failure after MOVE ownership settlement
cancel before/after stage admission
async suspend/restart/revalidation
feature-gate downgrade while cached plan exists
split-runtime fallback semantic equivalence
provider completion/publication/release races where gate enabled
```

Every race asserts both the permitted winner and zero leaked pins/leases/uses/fabricated closure.

## Normal-vs-fused differential suite

For every representative SIP chain execute:

```text
A) ordinary SIP boundaries
B) fused Job path
C) mixed path with forced materialization after every stage
D) split-runtime path where available
```

Compare declared observable semantics:

```text
result/error class
protocol state
capability consumption/quota
Region owner/generation/use state
sealed object lifecycle
cancellation disposition
final response publication
external operation lifecycle state
```

## Benchmark matrix

### Chain length

```text
2 / 3 / 4 / 8 stages
```

### Payload/data shapes

```text
empty/control
small bounded copied value
64 B / 4 KiB / 64 KiB / 1 MiB Region BORROW
64 B / 4 KiB / 64 KiB / 1 MiB Region MOVE
```

### Execution contours

```text
direct unsafe method baseline (measurement reference only, not security equivalent)
ordinary full SIP
fused linear Job
forced-materialization Job
read-only DAG serial (when gate enabled)
read-only DAG parallel (when gate enabled)
split-runtime Job (when gate enabled)
provider stage (when gate enabled; provider latency reported separately)
```

### Measurements

```text
throughput
median/p95/p99/max observed latency
allocations/op
GC bytes/op
queue enqueue/dequeue count
ChannelEnvelope/ResponseEnvelope count
Task/ValueTask completion-source allocation count
materialized invocation count
capability validation/admission count
session pin/revalidation count
Region transition/use count
worker/context switches where measurable
CPU/cache counters where tooling is reliable
```

Run shared and unrelated authority workloads at 1/2/4/8/16/32 workers, consistent with P13.

## Performance acceptance principles

No fixed speedup is promised in advance. Release gates should instead enforce:

1. fused path removes the intended intermediate transport materialization, proven by counters/traces;
2. no hidden per-element capability/Region lookup regression;
3. unrelated Job/Region work is not accidentally serialized by a new global Job lock;
4. disabled gates/fallback do not regress ordinary SIP beyond an agreed bounded tolerance;
5. performance report separates Job overhead from provider/hardware latency.

Any numeric regression thresholds must be calibrated on the release hardware/toolchain and stored with the exact tuple, not copied from the P13 machine.

## Read-only DAG release rule

`FG-READONLY-DAG` stays OFF unless all of the following pass:

```text
acyclic bounded graph verifier
read/read Region conflict proof
deterministic branch/join settlement
cancel/fault cleanup property tests
serial-vs-parallel semantic equivalence
1/2/4/8/16/32 worker contention report
no shared mutable/raw CLR graph path
```

`FG-MUTABLE-DAG` remains FutureGated even if read-only DAG qualifies.

## Claim/evidence artifacts

Produce machine-readable artifacts analogous to P13:

```text
P14_FEATURE_CLAIM_EVIDENCE_MATRIX.json
P14_SECURITY_QUALIFICATION_MATRIX.json
P14_PERFORMANCE_BASELINE.json
P14_TCB_AND_SUPPLY_CHAIN_REVIEW.md
P14_FUSION_CONFORMANCE_MATRIX.json
```

Record exact SingNextOS SHA, HybridCPU source/package SHA, .NET tuple, admission-policy digest and generated-plan schema version.

## Release criteria

Minimum release contour:

```text
QualifiedManaged: linear same-runtime generated ManagedCap Job
with bounded values and separately qualified BORROW/MOVE gates,
one final external publication, no raw CLR edges,
no external provider inside an inline fused segment unless its gate is separately qualified.
```

No `ProductionCandidate` claim is granted merely because tests pass on one workstation. Hardware behavior, independent VM/process isolation, side channels, real-time guarantees, multi-host behavior, NativeIsolated fusion, confidential-domain fusion and mutable DAG remain separately gated or FutureGated.

## Rollback

Feature gates default off. A failed qualification release disables the affected gate and uses ordinary SIP materialization; it does not reinterpret a previously stronger handle as weaker authority.
