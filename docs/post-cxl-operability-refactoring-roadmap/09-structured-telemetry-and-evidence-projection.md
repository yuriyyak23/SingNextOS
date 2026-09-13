# Phase 9 — Structured Telemetry and Evidence Projection

## Goal

Provide useful operational observability without ambient global introspection and without conflating telemetry with authority or security evidence.

## Projection model

Define typed projection classes:

```text
SelfOperationalTelemetry
ServiceAggregateTelemetry
PrivilegedSystemDiagnostics
SecurityEvidenceProjection
DebugTraceMetadata
```

Each projection requires an appropriate capability/visibility policy.

## Data model

Prefer typed snapshots/events over arbitrary key/value global namespaces. Core metrics include:

- CPU/budget consumption;
- owned memory and pinned/mapped memory;
- IPC queue depth, throughput, latency classes;
- active external operations by lifecycle stage;
- supervisor health/restart state;
- dependency degraded state;
- deadline expiration/cancellation counts;
- provider fault/reset categories;
- checkpoint duration/size/failures;
- trace buffer usage/drop markers;
- reclaim/pin counts where visibility allows.

## Telemetry versus evidence

Telemetry reports operational state. Evidence reports security/platform assertions with identity, freshness and assurance semantics.

Do not merge them into a single unrestricted event bus.

A telemetry record cannot satisfy a SecureCompute evidence requirement unless a separate evidence provider explicitly produces the required typed evidence.

## Host evidence non-leak

Default application projections exclude:

- physical topology;
- raw CXL fabric topology;
- other tenants/services identities and usage;
- host accelerator internal queue state;
- secure backend diagnostics;
- privileged provider recovery tokens.

Privileged diagnostics can expose more only through dedicated authority and policy.

## Subscription semantics

If streaming subscriptions are added, define:

- subscription identity/generation;
- owner/visibility class;
- bounded buffer/budget;
- drop/backpressure policy;
- close semantics;
- service-generation rebinding policy.

A restarted service receives a fresh subscription unless policy explicitly recreates it.

## Inspector and trace integration

Telemetry answers aggregate operational questions. Inspector answers current authority relationships. Trace answers history/causality.

Cross-links use correlation IDs; none of the three surfaces may silently acquire the semantics of another.

## Required tests

- self telemetry shows only own authorized data;
- cross-service projection denied without privilege;
- raw host/CXL topology absent from ordinary projection;
- secure evidence freshness preserved on evidence path;
- telemetry record cannot be used as evidence/capability;
- restarted service does not retain stale subscription generation;
- bounded telemetry buffer handles overflow explicitly;
- supervisor/IPC/budget/checkpoint metrics are internally consistent with source state.

## Exit criteria

Operational diagnosis is possible through typed, policy-controlled projections with no ambient host-evidence leak and strict telemetry/evidence separation.