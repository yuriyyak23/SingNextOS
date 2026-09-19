# Phase 11 — Fault, Reset, Reconfiguration, and Cancellation

## Goal

Make device reset, link/fabric reconfiguration, stale generation, cancellation, and transport failure first-class HybridCPU execution outcomes rather than exceptional afterthoughts.

## Failure classes

HybridCPU external-operation runtime should distinguish:

- pre-admission rejection;
- admitted-but-not-submitted cancellation;
- submit failure;
- device fault after submit;
- device reset/generation change;
- mapping/domain generation change when relevant;
- fabric binding/reconfiguration invalidation;
- visibility failure;
- publication rejection;
- transport/session loss;
- release/cleanup failure.

## Stage-sensitive fail-closed policy

### Prepared / Admitted

A stale generation or authority change blocks submit. Re-admission may be attempted if replay/owner policy allows it.

### Submitted

Do not issue a replacement operation until the original is proven cancelled/terminal or the provider contract guarantees idempotence/deduplication.

### DeviceComplete

Do not publish unless visibility and generation/publication validity can still be proven.

### Visible

Revalidate publication authority/generations before architectural commit.

### Published

Treat the effect as architectural. Later reset affects future operations, not retroactive rollback unless a separate persistence/transaction model explicitly provides it.

## Cancellation

Represent cancellation capabilities explicitly:

```text
NotSupported
BeforeSubmitOnly
BestEffortAfterSubmit
GuaranteedBeforeDeviceStart
ProviderDefinedIdempotentRetry
```

Do not infer successful cancellation from a local token state change.

## Reset handling

On reset/reconfiguration notification:

- invalidate cached external bindings;
- mark matching active tokens stale/fault-pending;
- prevent new submit using old receipts;
- drain/resolve submitted operations according to provider evidence;
- block publication of ambiguous results;
- invalidate replay reuse associated with old external generation identity.

## Reconfiguration

Fabric rebinding or Type-3 memory reconfiguration must remain opaque to HybridCPU topology-wise. The observable event is generation invalidation plus semantic capability/availability change.

## Compiler behavior

Compiler semantics should identify whether an operation permits retry/fallback, but not decide that a particular hardware reset is recoverable.

For example:

- retryable/idempotent external intent may be re-admitted after a confirmed terminal failure;
- non-idempotent intent becomes a fault/replay barrier if outcome is ambiguous;
- external-preferred operations may fall back only when language/runtime semantics permit equivalent CPU execution.

## Telemetry

Record enough correlation to diagnose:

- operation ID;
- descriptor identity;
- lifecycle stage at failure;
- old/new generation identity hashes;
- reset/reconfiguration reason class;
- whether any external effect may have occurred;
- whether publication occurred.

Do not log sensitive raw authority handles by default.

## Tests

Mandatory fault injection:

- reset before submit;
- reset immediately after submit;
- reset after DeviceComplete;
- generation drift before publication;
- transport loss while running;
- duplicate/late completion from pre-reset generation;
- cancellation race with completion;
- re-admission after confirmed cancellation;
- non-idempotent ambiguous completion blocks replay.

## Exit criteria

Every lifecycle stage has a defined response to stale authority, reset/reconfiguration, and cancellation, and no ambiguous result can be silently published or resubmitted.