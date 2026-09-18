# Phase 6 — Deterministic Tracing and OS-boundary Replay

Status: implemented and qualified on 2026-09-18. See
`06_PHASE_06_IMPLEMENTATION_EVIDENCE.md` for audited ownership, guarantees,
qualification results, and FutureGated event-family integrations.

## Goal

Provide causal observability and reproducible model execution at semantic OS boundaries while preserving the invariant that trace/replay evidence is never runtime permission.

## Trace identity model

Introduce:

```text
TraceSessionId / Generation
TraceProducerId
TraceSequence
CausalCorrelationId
TraceEventKind
TraceVisibilityClass
```

Per-producer sequences must be monotonic. Cross-producer causal relationships use explicit correlation IDs/parent edges rather than assuming timestamp total order.

## Required event families

Record semantic transitions for:

- service/process lifecycle and replacement;
- manifest admission;
- capability mint/delegate/revoke;
- region MOVE/borrow/return/revoke;
- RegionUse acquire/release;
- platform mapping/device lease lifecycle;
- IPC send/receive/ownership transfer correlation;
- budget reserve/release;
- deadline/cancellation events;
- ExternalOperation admission/submission/completion/visibility/publication/release;
- provider reset/generation change;
- virtualization/secure-domain transitions;
- checkpoint begin/commit/restore;
- supervisor health and dependency transitions.

## Sensitive data policy

Trace records contain semantic identities, digests, status and correlations. They must not persist reusable capability secrets, provider credentials, confidential payload contents or unrestricted host evidence.

Payload tracing defaults to metadata/digest only.

## Replay modes

### Diagnostic replay

Reconstruct and validate causal sequence without executing effects.

### Model replay

Feed recorded semantic inputs into deterministic model providers to reproduce lifecycle decisions.

### HybridCPU-correlated replay

Correlate HybridCPU retire/replay evidence with SingNextOS semantic operations. CPU replay proof remains evidence; every live OS effect in a replayed run requires fresh OS/provider admission.

### No automatic live re-effect replay

The framework must not offer “repeat recorded hardware effect” as a generic operation.

## Divergence detection

Replay reports typed divergence:

- generation mismatch;
- authority decision mismatch;
- dependency/order mismatch;
- provider semantic mismatch;
- publication mismatch;
- nondeterministic input not captured.

Divergence reporting is diagnostic and cannot force runtime state to match the trace.

## Buffering and performance

- tracing can be disabled;
- disabled path must avoid global synchronization;
- enabled path uses bounded per-producer buffering;
- trace budget is controlled by Phase 5;
- overflow policy explicit: drop-with-marker, backpressure for test mode, or stop session; never silently pretend completeness.

## Required tests

- deterministic event ordering per producer;
- causal chain capability -> IPC -> external operation -> publication;
- trace session generation stale handling;
- no live capability material serialized;
- replay cannot bypass current capability denial;
- model replay reproduces lifecycle result;
- HybridCPU replay evidence correlation does not authorize provider submission;
- overflow creates explicit incompleteness marker;
- trace visibility policy blocks unauthorized cross-service data.

## Exit criteria

A failed integration test can be explained by a bounded causal trace and replayed against model providers, while live effects still require normal authority/admission.
