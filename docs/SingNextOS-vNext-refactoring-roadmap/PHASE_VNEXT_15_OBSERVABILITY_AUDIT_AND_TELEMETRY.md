# P15 — Observability, audit and telemetry

## Goal

Expose enough evidence to debug resource authority without turning telemetry into policy or permission.

## Telemetry schema

Suggested event classes:

```text
ResourceCapabilityMinted/Derived/Revoked
ResourceLeaseReserved/Bound/Consuming/Settled/Released/Quarantined
ResourceDonationCreated/Returned
ResourceAdmissionDenied
ProviderUsageObserved
ResourceBudgetExhausted
ResourceGuaranteeMissed // only when such guarantee is actually qualified
```

## Correlation chain

Every external resource-consuming execution should support deterministic correlation:

```text
subject/process generation
 -> capability lineage id (redacted/opaque projection)
 -> resource lease
 -> endpoint invocation / SipJob stage
 -> ExternalOperation
 -> provider request/generation
 -> usage receipt
 -> local settlement
```

## Privacy/topology

Cross-tenant telemetry must redact provider topology/private identifiers. Public telemetry reports semantic resource class, not lane/queue/CXL path.

## Non-authority invariant

Telemetry subscription, performance report, scheduling trace or usage receipt does not mint/replenish authority.

## Metrics

Separate:

- admission latency;
- authority lock/linearization cost;
- scheduler policy cost;
- provider admission;
- execution;
- visibility/publication;
- settlement;
- SipJob transport savings.

Never collapse into one "vNext speedup" number.

## Exit criteria

Audit traces allow replay/debugging of decisions without exposing or recreating authority.
