# P14-2 — Direct Generated Sentry Path and Semantic-Trace Equivalence

## 1. Goal

Implement the narrowest same-runtime direct path while preserving the ordinary SIP security transition through an operation-specific generated sentry/static thunk.

The first executable contour remains two-stage, synchronous, copied/closed-value only, with no Region edge, no external effect, and no provider scheduling hint.

## 2. Security rule

The Job executor MUST NOT invoke a service implementation method directly.

Required shape:

```text
Job executor
    -> TCB-private exact binding lookup
    -> operation-specific generated sentry
       -> live owner validation/admission
       -> generated input projection
       -> protocol transition
       -> implementation invocation
       -> generated output validation/projection
       -> lifecycle/publication handling
```

The generated sentry is not a thin convenience wrapper. It is the compartment/security boundary.

## 3. TCB-private binding

A trusted binding record MAY contain:

```text
opaque service/process/session handles
precompiled thunk entrypoint/internal callable
TCB-private service implementation reference if the existing runtime dispatch model requires it
manifest/schema pointers internal to trusted runtime
```

It MUST NOT be:

- placed in `SipJobPlan`;
- placed in a ManagedCap-visible `JobRunFrame`;
- passed as a Job edge;
- returned to service code;
- used as evidence that authority remains valid.

Every authority owner still performs required live validation.

## 4. Normal-vs-fused semantic trace

For each operation, define a generated or manifest-bound semantic trace contract. The exact implementation order may vary only where existing normative semantics permit it.

Minimum comparison points:

| Semantic role | Ordinary SIP | Fused Job requirement |
|---|---|---|
| caller/process incarnation | validate exact generation | same owner / equivalent live validation |
| session lifecycle | current session checks/pins | same generation/state semantics |
| capability/effect | exact operation admission | same owner and consumption semantics |
| sealing | validate/pin when required | same owner and timing constraints |
| input isolation | transport projection/copy | equivalent generated projection/copy |
| Region use/ownership | none in initial contour | introduced only under P14-3 gate |
| protocol state | authoritative transition | same transition linearization |
| cancellation | existing invocation/session semantics | equivalent allowed race outcomes |
| implementation entry | after successful checks | no earlier than ordinary semantic admission |
| output validation | generated SIP schema | same generated constraints |
| publication | final ordinary response | same final publication owner |

Qualification compares event order, not only final state.

## 5. Checks that MUST NOT be hoisted unsafely

A static verifier/cache may precompute where a check occurs, but it MUST NOT turn these into historical booleans:

- process/service generation alive;
- session active/generation current;
- capability not revoked/quota available/one-shot unused;
- seal live;
- protocol state current;
- Region generation/use live;
- provider generation/admission live.

Checks can be omitted at runtime only when an existing owner-issued opaque pin/lease is explicitly specified to guarantee the corresponding property for its lifetime.

## 6. Copy/isolation rule

Fusing a copied edge may remove envelope serialization/materialization, but it may not remove isolation semantics.

Cases:

1. **immutable/value-only projection:** may be passed directly if the generated schema proves alias-free/value semantics;
2. **mutable reference-backed input:** generated snapshot/deep-copy/projection remains required;
3. **large shared data:** use a separately qualified BORROW/MOVE Region contour rather than a raw reference shortcut.

Negative test: caller mutates source immediately after crossing the fused boundary. Callee observation must match ordinary SIP snapshot semantics.

## 7. Cancellation semantics

The direct path must identify a cancellation linearization point compatible with ordinary SIP.

At minimum test:

```text
cancel before stage admission
cancel after session pin but before capability commit
cancel immediately before implementation entry
cancel concurrent with implementation completion
cancel after completion but before publication
```

A Job cancellation token/object is not authority and must not bypass existing invocation/session owners.

## 8. Protocol semantics

Protocol transitions remain owned by the existing protocol/session/channel owner.

The Job executor MUST NOT infer protocol state from stage index.

A transition that ordinary SIP commits before service entry or before a later independent invocation must be committed at the equivalent semantic point in the fused path.

## 9. Error mapping

Fused errors must map to the same public error/denial classes as ordinary SIP for the qualified contour. Internal fast-path diagnostics may differ but must not expose implementation references or authority internals.

Required classes include:

- invalid/stale process;
- invalid/stale session;
- capability denied/revoked/exhausted;
- seal denied;
- protocol violation;
- malformed projected value;
- cancellation;
- service fault.

## 10. JIT and NativeAOT

P14-2 may initially qualify JIT only or NativeAOT only, but the claim must say which.

Separate evidence is required for:

- generated thunk identity/linkage;
- reflection/trimming behavior;
- async later in P14-5B;
- static field/reference reachability;
- manifest integrity.

No claim may infer NativeAOT qualification from JIT success or vice versa.

## 11. Executable proof set

### Differential success/fault tests

Run the same operation via ordinary SIP and fused direct sentry. Compare:

- returned closed values;
- public error class;
- protocol state;
- capability state;
- final publication state;
- semantic trace.

### Race injection tests

Inject restart/revoke/session-close/cancel immediately before and after each authoritative transition.

Expected result: fused set of legal outcomes is no broader than the ordinary SIP set.

### Raw-reference escape tests

Generated fixtures attempt to store/pass:

- implementation `this`;
- delegate;
- service dependency;
- `object` payload;
- mutable static reference.

Expected result: static rejection or no reachable field in Job-visible state.

## 12. Fallback

Any condition not proven under `FG-DIRECT-SENTRY` routes to ordinary SIP. No "best effort" direct call exists.

## 13. PR decomposition

1. generated direct-thunk contract with gate permanently OFF;
2. TCB-private binding table;
3. semantic trace instrumentation for fused path;
4. two-stage closed-value executor in test-only gate;
5. differential/race/escape tests;
6. qualification artifact for the exact runtime mode.

Each PR must allow disabling/reverting the direct path without changing ordinary SIP.

## 14. Exit criteria

`FG-DIRECT-SENTRY` may reach `RuntimeEnforced` only after negative/race tests pass, and `QualifiedManaged` only after the exact contour has differential semantic-trace qualification.
