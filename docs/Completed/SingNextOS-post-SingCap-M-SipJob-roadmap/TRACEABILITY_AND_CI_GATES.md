# SipJob Traceability and CI Gates

This document binds normative requirements to executable evidence. A feature is not runtime-enforced merely because a descriptor, parser, generator model, telemetry field, test fake, or design document exists.

## 1. Evidence categories

| Evidence | Purpose |
|---|---|
| static negative fixture | prove unsupported source/schema/reference forms fail before execution |
| runtime negative test | prove live owners deny stale/revoked/invalid execution |
| race test | force adversarial ordering around a linearization point |
| property test | explore many graph/lifecycle/ownership terminal combinations |
| differential conformance test | compare ordinary SIP and fused execution semantics |
| manifest/supply-chain check | bind generator/analyzer/runtime/provider artifacts to exact tuple |
| performance benchmark | measure cost/savings without replacing security evidence |

## 2. Required semantic-trace instrumentation

Qualification builds MUST expose test-only semantic events sufficient to compare ordinary and fused paths. Instrumentation MUST observe owner linearization outcomes without itself becoming an authority source.

Minimum logical event classes:

```text
process/service generation validation
session prepare/pin/revalidation
capability/effect commit/consumption
seal validation/pin
Region use acquisition/release
Region ownership transfer + generation
protocol transition
invocation/cancellation transition
implementation entry/exit
provider submission/completion/visibility
response publication
ownership settlement
release/cleanup
```

Transport-only event classes are tracked separately:

```text
envelope allocation
queue enqueue/dequeue
waiter/TCS allocation
scheduler wake/suspend/resume
discovery/cache lookup
```

The differential oracle MUST compare authoritative-event order after filtering only a documented transport whitelist.

## 3. CI lanes

### Lane A — static closed-world admission

Runs on every PR touching Job descriptors, generators, analyzers, admission rules, SIP schemas, or service contracts.

Required negative fixtures:

- raw service implementation reference;
- delegate/closure;
- `IServiceProvider`/service locator;
- mutable `object` graph;
- reflection/dynamic activation;
- unknown thunk/schema/barrier version;
- service-created awaitable used as edge/frame state;
- mutable static authority root;
- forbidden generic container.

Expected result: compile/admission rejection with stable diagnostic class.

### Lane B — linear synchronous semantic conformance

Runs ordinary SIP and fused execution with identical input/authority setup.

Cases:

- success;
- service exception;
- invalid input projection;
- stale process/service generation;
- stale session generation;
- revoked capability;
- exhausted quota;
- consumed one-shot;
- closed seal;
- protocol-state mismatch;
- cancel before stage;
- cancel at stage boundary.

Expected result: same allowed/denied/fault/cancel class and equivalent authoritative transition trace.

### Lane C — authority composition races

Force races at each preparation/commit boundary:

1. revoke capability before final revalidation;
2. revoke/consume one-shot concurrently with two Jobs;
3. session close before pin;
4. session close after pin but before commit;
5. seal close/restart during prepare;
6. Region reclaim during preparation;
7. process/service restart after cached binding resolution;
8. second non-compensatable participant becomes unavailable.

Expected result:

- no premature quota/one-shot consumption for a stage that never reaches its defined commit;
- exactly one winner for one-shot authority;
- no fabricated compensation;
- no service/provider code under owner locks;
- fallback/materialization where safe composition cannot be guaranteed.

### Lane D — Region ownership/use

Required MOVE/BORROW scenarios:

```text
success
exception
cancel before consumer stage
cancel during consumer stage
service restart
reclaim race
partial branch completion
join failure
provider ambiguity
```

Assertions:

- exact owner sequence;
- exact generation progression;
- no double MOVE;
- no implicit inverse MOVE;
- no lost owner;
- no stale view rematerialization;
- no early reclaim;
- no leaked borrow/use lease.

### Lane E — cache/restart/ABA

Required mutations between cache fill and execution:

- runtime/authority realm recreation;
- process/service incarnation restart;
- session ID reuse with new generation;
- capability revoke/rederive/new lineage;
- resource generation change;
- seal recreation;
- contract/schema/thunk digest change;
- admission-policy/toolchain digest change;
- plan digest change;
- provider generation/contract change for provider-dependent stages.

Expected result: stale cache entries miss/rebind/deny; never execute based on historical `authorized=true`.

### Lane F — async

Required fixtures:

- incomplete `Task` capturing `this`;
- `ValueTask` backed by service-private object;
- custom awaitable capturing mutable dependency;
- cancellation before await, during wait, and concurrent with completion;
- session close/restart while suspended;
- Region borrow lifetime across suspension;
- provider completion ambiguity.

Expected result: Job frame contains only allowed TCB-owned correlation state; resumption is generation/lifecycle safe.

### Lane G — read-only DAG and parallel DAG

Property dimensions:

```text
branches: 2 / 4 / 8
workers: 1 / 2 / 4 / 8 / 16 / 32
branch result: success / fault / cancel
join: all-success / defined fault / defined cancel
shared read: copied immutable / qualified BORROW
```

Assertions:

- deterministic join policy;
- no branch observes mutable sibling output;
- shared lifetime closes only after last dependent branch settles;
- no early ownership settlement;
- no hidden shared mutable state;
- no worker-dependent semantic outcome.

### Lane H — provider/HybridCPU

Required tests:

- local admission denied, platform would accept → no submission;
- local admission accepted, platform denied → fallback/deny according to contract;
- stale provider generation;
- unknown semantic hint;
- provider completes but visibility not established;
- completion without publication permission;
- unsupported accelerator contour.

Expected result: platform evidence never becomes SingNext authority; publication waits for all required owner states.

### Lane I — performance

Performance CI runs only after security/conformance lanes pass for the same build tuple.

It records ordinary SIP, fused Job, direct-call diagnostic baseline, forced-materialization, and split-runtime fallback as applicable.

## 4. Requirement-to-evidence matrix

| Invariant | Minimum CI evidence |
|---|---|
| SJOB-001 | B, C, E, H |
| SJOB-002 | A, B, F |
| SJOB-003 | A, B |
| SJOB-004 | B, D, H |
| SJOB-005 | A, B, C |
| SJOB-006 | E |
| SJOB-007 | D, G |
| SJOB-008 | B, C, H |
| SJOB-009 | C |
| SJOB-010 | A, F |
| SJOB-011 | B, F, G, H |
| SJOB-012 | B, D, F, G, H |
| SJOB-013 | A, E |
| SJOB-014 | B, G |
| SJOB-015 | B, D, H |
| SJOB-016 | all lanes relevant to the promoted contour |

## 5. Mandatory race injection points

The test runtime SHOULD expose deterministic hooks immediately before and after:

```text
session pin
session final revalidation
capability commit/acquisition
seal pin/revalidation
Region use acquire
Region transfer
protocol transition
implementation entry
external submit
provider completion
visibility confirmation
response publication
ownership settlement
release
```

A race test is not sufficient if it only relies on probabilistic sleeps.

## 6. Supply-chain and manifest gates

Qualification artifacts MUST bind:

- SingNextOS source SHA;
- generator source/binary digest;
- analyzer/admission-tool source/binary digest;
- .NET SDK/runtime/NativeAOT tuple as applicable;
- normative schema/manifest digest;
- admission-policy digest;
- plan/thunk catalog digest;
- HybridCPU/provider source/package/version/digest for provider-dependent contours;
- enabled feature-gate set.

Any mismatch invalidates the qualification claim for that artifact.

## 7. Claim gates

A document or release note MAY say:

- `ModelOnly` only when design exists;
- `StaticAdmission` only when CI Lane A has executable rejection evidence;
- `RuntimeEnforced` only when live owner negative/race tests pass;
- `QualifiedManaged` only after all contour-relevant lanes plus performance characterization pass on the exact tuple;
- `ProductionCandidate` only after rollback/fallback/observability/deployment evidence also exists.

The terms `zero-copy`, `O(1)`, `transactional`, `atomic Job`, `hardware-enforced`, `confidential`, or `production-ready` MUST NOT be used unless the exact contour has direct executable evidence supporting that specific claim.

## 8. Failure policy

Any failure in static admission, semantic differential tests, race/property tests, cache ABA tests, or lifecycle cleanup blocks gate promotion regardless of benchmark improvement.

Performance regressions do not justify weakening an authority or isolation check. The fallback is ordinary SIP or gate disablement.
