# P14 SipJob Traceability and CI Gates

## Requirement-to-phase map

| Requirement | Primary phase | Mandatory executable evidence |
|---|---|---|
| SJOB-001 no new authority universe | P14-0/P14-1 | architecture dependency test; no parallel authority registry |
| SJOB-002 no raw CLR references | P14-0/P14-1/P14-2 | generator/analyzer negatives; hostile compiled fixture |
| SJOB-003 generated sentry remains transition | P14-2 | normal-vs-fused sentry conformance tests |
| SJOB-004 transport elision only | P14-2 | exact checks preserved; queue/response elision instrumentation |
| SJOB-005 authority source per stage | P14-1/P14-4 | confused-deputy negatives |
| SJOB-006 cache where-to-check only | P14-6 | revoke/restart/stale generation invalidation tests |
| SJOB-007 terminal-path ownership proof | P14-3/P14-5 | property tests over success/fault/cancel graphs |
| SJOB-008 not transactional | P14-0/P14-5 | failure-after-private-mutation and provider submission tests |
| SJOB-009 segmented admission | P14-4 | one-shot/quota not consumed for unreached stage |
| SJOB-010 no Span across await | P14-5 | analyzer/compiler/runtime async lease tests |
| SJOB-011 explicit barriers | P14-5 | planner golden tests; unknown barrier fail closed |
| SJOB-012 normal SIP semantic oracle | P14-2/P14-8 | differential/conformance suite |
| SJOB-013 no dynamic codegen | P14-6 | ManagedCap AdmissionVerifier fixtures |
| SJOB-014 protocol-state preservation | P14-2/P14-4 | invalid transition and race tests |
| SJOB-015 observable lifecycle materialization | P14-5 | cancellation/publication/provider observer tests |
| SJOB-016 contour-specific claims | P14-8 | machine-readable claim/evidence matrix |

## Required CI from first P14 semantic PR

```text
locked restore + deterministic build
existing P02-P13 security/concurrency suites
generator/analyzer goldens
ManagedCap full-module admission fixtures
NativeAOT selected fixture
architecture dependency checks
normal-SIP vs fused differential tests
Job graph verifier property tests
Region ownership terminal-path property tests
session/capability/seal/Region race tests
no-provider-under-authority-lock checks
feature-gate default-off tests
```

From P14-5 add:

```text
branch/join cancellation tests
read-only DAG Region conflict tests
parallel branch deterministic completion/join tests
async suspension/resume lease revalidation tests
```

From P14-7 add:

```text
HybridCPU/provider package digest/source pin
provider-neutral scheduling contract tests
no lane/opcode/topology leakage architecture test
external lifecycle conformance where enabled
```

P14-8 makes the following release-blocking:

```text
single-thread latency
1/2/4/8/16/32 worker shared + unrelated contention
allocations and GC bytes/op
queue/envelope/materialized-invocation counts per Job
normal SIP vs fused Job throughput/latency
BORROW/MOVE payload-size sweep
split-runtime fallback conformance
fault/cancel/close/revoke/restart stress
claim/evidence matrix validation
```

## PR security checklist

Every P14 PR answers:

```text
Does this add a new identity/generation? Who owns it?
Can the new identity accidentally become authority?
Is any permission/session/Region/seal/publication truth duplicated?
Can a raw CLR reference or implementation object cross a ManagedCap edge?
Which exact generated sentry runs for each fused transition?
Which existing registry owns each live pin/lease?
What is the prepare/pin/commit point?
What authority is consumed only when a stage is actually reached?
What are success/fault/cancel ownership settlement paths?
Which FusionBarrier classes apply?
Is any intermediate lifecycle externally observable and therefore materialized?
Can provider/user code execute under an authority lock?
What invalidates any plan/binding cache?
Does normal SIP fallback preserve semantics?
Which feature gate controls the new contour?
What claim level is justified by executable evidence?
```
