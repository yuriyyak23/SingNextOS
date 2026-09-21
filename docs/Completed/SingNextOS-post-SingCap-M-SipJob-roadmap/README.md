# SingNextOS post-SingCap-M — SipJob Refactoring Roadmap

**Status:** proposed implementation roadmap. No runtime, security, performance, qualification, or production claim is implied until the corresponding executable gate has passed.

**Scope:** introduce `SipJob` as a selectively fused execution path for two or more ManagedCap SIP stages inside one qualified managed runtime while preserving the existing SingCap-M authority, ownership, lifecycle, protocol, external-effect, and publication owners.

## 1. Architectural objective

`SipJob` is an execution-composition optimization over existing SIP contracts. It may eliminate intermediate transport machinery only when the ordinary SIP path and the fused path are proven semantically equivalent at every authoritative transition.

```text
ordinary SIP edge
    = authoritative security transition(s)
    + isolation-required value projection/copy
    + Region ownership/use transition(s), when declared
    + protocol transition(s)
    + invocation/cancellation lifecycle, when observable
    + transport materialization
    + scheduler/queue/waiter machinery
    + publication boundary, when observable

qualified fused SipJob edge
    = the same authoritative security transition(s)
    + the same isolation-required value semantics
    + the same Region ownership/use transition sequence
    + the same protocol transition(s)
    + the same observable lifecycle semantics
    + generated trusted dispatch

    minus only transport/scheduler/materialization work whose
    observability has been explicitly proven unnecessary.
```

The optimization boundary is therefore **transport materialization**, not authority, ownership, protocol, isolation, or publication semantics.

## 2. Source-of-truth discipline

All implementation work is evaluated against the following trust order:

```text
live production code + executable tests
    >
current normative specification
    >
current implementation evidence
    >
roadmap/design documents
    >
historical/vision material
```

Parser/model/helper/DTO/test-fake/telemetry/documentation presence is not runtime-enforcement evidence.

The first implementation phase must pin the exact source/tooling tuple before semantic code lands. The source snapshot recorded for this roadmap is:

```text
Audit date:        2026-09-19
SingNextOS HEAD:   0f152a5502c58546eb0458b4be3e7cbce6c5ef3e
HybridCPU-v2 HEAD: 90329e2e342575953ce66e741f3c1f223ff577a0
```

These values are inputs to P14-0, not automatic qualification. P14-0 must re-confirm them or record newer exact SHAs together with the full toolchain/admission/provider tuple.

## 3. Existing owners remain authoritative

`SipJob`, `SipJobPlan`, `SipJobHandle`, `JobRunFrame`, descriptors, plan digests, caches, worker-local state, scheduling hints, and completion summaries are not authority owners.

| Truth | Existing owner |
|---|---|
| capability/effect permission, lineage, revocation, quota, one-shot consumption | `CapabilityAuthority` |
| process/service incarnation | `ProcessRegistry` |
| endpoint session lifecycle and generation | `EndpointSessionRegistry` |
| observable invocation/cancellation state | existing invocation owner, including `EndpointSessionInvocationRegistry` where applicable |
| Region ownership, generation, borrow/use state | `RegionAuthority` |
| sealed-object identity/liveness | `SealedObjectAuthority` |
| external-effect lifecycle | `ExternalOperationAuthority` |
| response/publication lifecycle | existing response/invocation/publication owners |
| HybridCPU/provider legality and execution admission | external platform/provider legality/admission owners |

A Job object may carry only non-authoritative references needed to locate these owners and revalidate current state.

## 4. Core safety model

### 4.1 No raw ManagedCap reference crossing

Across every ManagedCap SIP or Job boundary, the following remain forbidden:

- service implementation object references;
- arbitrary mutable CLR object graphs;
- delegates or delegate targets;
- `IServiceProvider`, service locators, dependency containers, or ambient authority objects;
- mutable reference-bearing static/global state exposed to ManagedCap code;
- reflection or dynamic activation surfaces not admitted by the closed-world policy;
- service-created `Task`, `ValueTask`, custom awaitable, closure, or async state-machine object used as a Job edge value;
- generic object containers that can smuggle any of the above.

Trusted runtime dispatch tables may contain implementation references, method entrypoints, or generated delegate-like artifacts only as **TCB-private** implementation details. They must never appear in `SipJobPlan`, `JobRunFrame`, edge state, ManagedCap-visible state, or a cache whose contents can be projected into ManagedCap code.

### 4.2 Generated sentry remains the security transition

Each fused stage enters through an operation-specific generated static sentry/thunk. The sentry must preserve the security semantics of the ordinary generated SIP path, including current process/session generation, exact capability/effect admission, sealing, Region use/ownership transitions, protocol state, cancellation semantics, output validation, and publication rules.

Dynamic IL/runtime code generation is not permitted on the ManagedCap path. Dynamic composition selects only precompiled, admission-qualified thunks.

### 4.3 Security-transition trace equivalence

Qualification compares an ordered semantic trace rather than only final state. A normal SIP execution and a fused execution may differ in transport-only events, but every authoritative event must be preserved or be covered by an explicit proof that it is not semantically observable.

Canonical event classes include:

```text
SubjectGenerationValidated
SessionPreparedOrPinned
SessionGenerationValidated
OperationAuthorityCommitted
SealValidatedOrPinned
RegionUseAcquired
RegionOwnershipTransferred
ProtocolTransitionCommitted
ImplementationEntered
ImplementationCompletedOrFaulted
OutputValidated
InvocationStateTransitioned
CancellationLinearized
ResponsePublished
OwnershipSettled
VisibilityConfirmed
ReleaseCompleted
```

Exact event names are implementation-defined; their semantic roles are not.

## 5. Corrected authority-composition rule

The composition protocol is:

```text
resolve non-authoritative identities
    -> prepare/probe reversible participants
    -> pin/reserve reversible participants
    -> final live revalidation
    -> commit non-compensatable/consumptive participants
    -> release authority locks
    -> execute provider/service code
    -> settle ownership/protocol/publication
    -> release remaining pins/leases in reverse dependency order
```

A current `OperationAuthorityLease` that consumes quota or one-shot state during acquisition is a **commit participant**, not a reversible prepare participant. Disposing it does not imply semantic compensation.

If a proposed fused segment would require two or more independently fallible non-compensatable commits and existing owners do not provide a safe reservation/commit protocol, that segment is not fusion-qualified. Insert a materialization barrier or use the ordinary SIP path.

No service, provider, callback, continuation, or user code may run while an authority-owner lock is held.

## 6. Region rule

Fusion must preserve the complete ordinary Region ownership/use transition sequence, not merely the final owner.

For example, if ordinary composition performs:

```text
StageA responder -> caller
caller -> StageB receiver
```

then a fused Job cannot replace it with:

```text
StageA responder -> StageB receiver
```

unless a separately specified contract contour proves that the intermediate ownership state never exists in the ordinary protocol. Region generation advances, stale-handle behavior, reclaim races, and owner identity are security semantics.

## 7. Transaction and publication rule

`SipJob` is not ACID. It does not imply rollback of:

- private service mutations;
- consumed capability quota/one-shot state;
- committed Region transfers;
- irreversible provider submissions;
- externally published results.

Stage completion, provider completion, visibility, publication, ownership settlement, and release remain distinct states.

An irreversible private service mutation is a segment commit/barrier condition even if it has not yet produced an external response, because later calls may observe that mutation.

## 8. HybridCPU boundary

The Job may supply only semantic information such as:

```text
dependency graph
execution class
Region/use semantics
semantic compute request
```

The Job ABI must not expose physical or provider-private details such as lane IDs, raw opcodes, VMCS identifiers, IOMMU handles, provider tokens, accelerator-private handles, or CXL topology.

The system-level admission rule remains:

```text
LocalAllowed =
    exact local capability
  + exact subject/resource generation
  + ownership/session/object state

PlatformAllowed =
    independent live provider/platform admission

EffectAllowed =
    LocalAllowed && PlatformAllowed
```

A new semantic hint that crosses the existing provider boundary is a new versioned external contract unless it losslessly maps to an already-qualified provider-neutral contract.

## 9. Phase dependency graph

```text
P14-0 Architectural freeze, exact baseline tuple, ADR/security-trace vocabulary
   |
   +--> P14-1 Immutable plan/graph model + linear closed-world verifier
           |
           +--> P14-2 Generated direct sentry + semantic-trace conformance
                   |
                   +--> P14-3 Region BORROW/MOVE sequence preservation
                           |
                           +--> P14-4 Corrected composed admission + segmentation
                                   |
                                   +--> P14-5A Barrier model
                                   |      |
                                   |      +--> P14-5B Async contour
                                   |      +--> P14-5C Read-only DAG semantics
                                   |              |
                                   |              +--> P14-5D Parallel DAG qualification
                                   |
                                   +--> P14-6 Dynamic binding + non-authoritative caches
                                           |
                                           +--> P14-7 Provider-neutral scheduling integration
                                                   |
                                                   +--> P14-8 Security/performance/claim closure
```

The P14-5 sub-contours have independent gates and independent rollback. A qualified linear synchronous Job does not qualify async, DAG, parallel, external-effect, split-runtime, or hardware-accelerated execution.

## 10. Phase files

| Phase | File | Primary result |
|---|---|---|
| P14-0 | `PHASE_14_0_ARCHITECTURAL_FREEZE.md` | exact baseline/toolchain tuple, authority map, security-trace contract, rollback/fallback rules |
| P14-1 | `PHASE_14_1_PLAN_AND_GRAPH_VERIFIER.md` | immutable non-authoritative descriptors, canonical digest, linear MVP verifier |
| P14-2 | `PHASE_14_2_DIRECT_GENERATED_SENTRY_PATH.md` | direct trusted sentry preserving ordinary SIP security semantics |
| P14-3 | `PHASE_14_3_REGION_AND_OWNERSHIP_EDGES.md` | exact Region transition sequence and terminal-path ownership proof |
| P14-4 | `PHASE_14_4_COMPOSED_ADMISSION_AND_SEGMENTS.md` | reversible prepare vs consumptive commit, multi-owner segmentation |
| P14-5 | `PHASE_14_5_FUSION_BARRIERS_ASYNC_AND_DAG.md` | complete barrier model plus independently gated async/read-only DAG/parallel contours |
| P14-6 | `PHASE_14_6_DYNAMIC_BINDING_AND_PLAN_CACHE.md` | precompiled dynamic binding, realm/incarnation-safe cache invalidation |
| P14-7 | `PHASE_14_7_HYBRIDCPU_SCHEDULING_INTEGRATION.md` | semantic scheduling hints without CPU/provider authority leakage |
| P14-8 | `PHASE_14_8_QUALIFICATION_PERFORMANCE_AND_CLAIMS.md` | differential security qualification, contention/performance evidence, bounded claims |

Cross-cutting normative requirements are in `SIPJOB_NORMATIVE_INVARIANTS.md`, gate promotion rules in `FEATURE_GATES.md`, and executable evidence ownership in `TRACEABILITY_AND_CI_GATES.md`.

## 11. Initial implementation contour

The first semantic implementation contour is intentionally smaller than the eventual feature envelope:

```text
2 stages
same qualified managed runtime
synchronous
precompiled generated sentries
bounded closed copied/value forms only
no Region transfer
no external effect
no async
no DAG
no provider scheduling hint
one final externally observable publication
no independently observable intermediate invocation
```

It exists to prove generated-sentry and semantic-trace equivalence before Region, composed admission, async, DAG, or platform interactions are introduced.

All later contours are default-off until their own executable evidence exists.
