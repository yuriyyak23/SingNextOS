# SipJob Normative Invariants

This file is normative for all P14 phases. A P14 implementation that violates any invariant below is not a SipJob optimization; it is a new security model and must not be merged under this roadmap.

## SJOB-001 — Job metadata is evidence, never authority

`SipJobPlan`, `SipJobHandle`, `PlanDigest`, `StageDescriptor`, `EdgeDescriptor`, `JobRunFrame`, stage completion records and plan caches may correlate exact live authority, but they MUST NOT answer “is this caller allowed?” independently.

Allowed reference graph:

```text
JobRunFrame
  -> OperationAuthorityLease (CapabilityAuthority-owned)
  -> EndpointSessionPin (EndpointSessionRegistry-owned)
  -> RegionUse/Borrow/Region handle (RegionAuthority-owned)
  -> SealedObjectPin (SealedObjectAuthority-owned)
  -> ExternalOperation correlation (ExternalOperationAuthority-owned)
```

Forbidden:

```text
JobCapabilityAuthority
JobRegionAuthority
JobSessionAuthority
JobSealAuthority
JobPublicationAuthority
```

## SJOB-002 — no raw CLR graph crossing

A Job edge MUST be one of the generated, closed forms already acceptable to SIP or explicitly qualified by P14:

```text
primitive / enum / bounded generated copied value
opaque generated handle
OwnedBuffer<T> / OwnedRegion<T>
BorrowLease / qualified Region projection
sealed handle
trusted runtime-only stage evidence (never projected to ManagedCap)
```

Forbidden across ManagedCap boundaries:

```text
arbitrary object
mutable class graph
service implementation instance
delegate/function object
IServiceProvider / service locator
Task carrying hidden authority object graph
reflection handle used to reach private service state
```

## SJOB-003 — generated sentry cannot be skipped

Every cross-compartment Job stage transition MUST execute an operation-specific generated trusted sentry or an exactly equivalent generated static thunk. The thunk validates/project current authority and does not expose enumerable capability context.

## SJOB-004 — fusion is transport elision only

Fusion MAY remove intermediate:

```text
ChannelEnvelope allocation
queue enqueue/dequeue
ordinary response envelope
TaskCompletionSource/waiter
scheduler wake-up
service discovery already bound by the plan
```

Fusion MUST NOT remove required:

```text
session generation/state check or exact pin semantics
capability operation admission
seal identity/lifecycle check
Region ownership/use transition
protocol state transition
external-effect lifecycle gate
publication/release gate
```

## SJOB-005 — exact authority source per stage

Every authority requirement declares its source class:

```text
CallerExplicit
DelegatedFromCaller
ServiceManifestStatic
PreviousStageDeclaredReturn
KernelOnly
```

No stage may use privilege merely because the trusted Job executor happens to possess it. `PreviousStageDeclaredReturn` is valid only when the source SIP contract explicitly returns/delegates that authority.

## SJOB-006 — plan binding caches where to check, not “authorized=true”

A bound plan may cache exact record references/keys, generated thunk IDs, schemas and expected generations/epochs. Each run must still revalidate live generation/state/epoch/lease rules. Any cache invalidation ambiguity fails closed or materializes the ordinary SIP path.

## SJOB-007 — ownership is linear on every terminal path

For each Region-bearing edge, the verifier must prove that every success/fault/cancel terminal path yields a defined legal state:

```text
exactly one mutable owner after MOVE
no use-after-MOVE
no double consume
borrow closed/revoked according to lifetime
active RegionUse settled or quarantined
no reclaim while external ambiguity remains
```

Failure is not implicit inverse MOVE.

## SJOB-008 — Job is not a transaction

A Job may defer externally visible publication where existing staged semantics allow it, but it cannot roll back arbitrary service-private mutation. Provider submission cannot be undone by metadata rollback. The plan must expose barriers where irreversible effects may begin.

## SJOB-009 — admission is segmented

One-shot rights, quotas and effect-specific leases are acquired at the latest safe segment admission point, not blindly for the entire future graph. Conditional/unreached stages MUST NOT consume authority simply because they appear in a plan.

## SJOB-010 — async does not retain stack views

No `Span<T>`, `ReadOnlySpan<T>` or ref-like projection survives suspension. Heap-safe leases remain authoritative references; after resume the runtime revalidates and rematerializes the stack view.

## SJOB-011 — explicit FusionBarrier classes

At minimum the planner/executor recognizes:

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

A barrier may force ordinary invocation materialization or segment commit. Unknown barrier requirement fails closed.

## SJOB-012 — ordinary SIP fallback is semantic reference

For every fused stage sequence there must be a normal-SIP execution mode used as a conformance oracle. Same valid inputs/authority must yield equivalent declared outputs/ownership/publication semantics, modulo performance/tracing identifiers.

## SJOB-013 — no dynamic code generation in ManagedCap path

P14 may dynamically select/combine **precompiled, admission-qualified generated thunks**. It MUST NOT require `Reflection.Emit`, runtime IL generation, arbitrary assembly loading, unmanaged calli or policy-bypassing dynamic code.

## SJOB-014 — protocol state remains authoritative

Direct dispatch may bypass the physical channel queue only after validating/applying the same protocol-state transition owned by the existing protocol/session machinery. Job plan metadata cannot independently advance protocol state.

## SJOB-015 — independently observable lifecycle requires materialization

If an intermediate stage requires independent cancellation, external observation, provider callback, response publication or durable correlation, its invocation lifecycle must be materialized in the existing invocation/response owners rather than hidden as a lightweight Job-only state.

## SJOB-016 — claims are contour-specific

Qualification of linear synchronous same-runtime fusion does not qualify DAG, parallelism, provider fusion, split-runtime execution, NativeIsolated fusion, confidential execution or HybridCPU acceleration. Each remains at its declared feature-gate claim level.
