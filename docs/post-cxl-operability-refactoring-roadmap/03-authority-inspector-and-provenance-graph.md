# Phase 3 — Authority Inspector and Provenance Graph

## Goal

Provide a safe read-only explanation layer for SingNextOS authority: who owns a resource, how authority was delegated, what pins reclaim, and why a lifecycle transition is blocked.

## Architectural rule

Inspector is observation only:

```text
Inspector snapshot != capability
Inspector edge != authority
Inspector evidence != permission
```

The implementation must not add mutation endpoints to the inspector surface.

## Graph node families

At minimum support nodes for:

- service instance / process / domain;
- capability record;
- owned region;
- borrow lease;
- RegionUse;
- backing lease;
- platform mapping;
- device lease and DMA grant;
- ExternalOperation;
- VirtualDomain and SecureDomain;
- checkpoint pin;
- budget reservation;
- supervisor dependency binding.

## Edge families

Use typed edges such as:

```text
owns
minted-by
delegated-to
borrowed-by
mapped-into
backed-by
pins
requires
waiting-for-closure
authorized-by
quarantined-by
replaced-by
```

Every edge must be derived from authoritative runtime state but remain a detached projection.

## Snapshot semantics

Define consistency classes, for example:

- `PointInTimeBestEffort` — cheap diagnostic snapshot;
- `AuthorityLockedSnapshot` — bounded strongly consistent snapshot for tests/admin diagnostics;
- `HistoricalReference` — trace-correlated observation, never live state.

Every snapshot includes generation metadata and capture sequence/time.

## Query surfaces

Required queries:

```text
InspectOwner(resource)
InspectDependents(resource)
InspectCapabilityProvenance(capability)
WhyMoveBlocked(region)
WhyReclaimBlocked(region/process)
WhyServiceDrainBlocked(service)
WhyExternalOperationPinned(operation)
```

The `Why*` APIs should return typed reasons rather than free-form logs only.

## Visibility policy

Default process-visible inspection is self-scoped. Cross-service/system graph access requires dedicated inspection authority.

Redact or suppress by policy:

- other-tenant identity details;
- physical host topology;
- raw secure backend diagnostics;
- provider-private tokens;
- CXL internal topology identifiers unless a dedicated privileged diagnostic projection explicitly allows them.

## No reusable authority in DTOs

Inspector DTOs should carry display/correlation identities and generations, not objects that can be passed directly to effect APIs as live permission.

If an existing handle type is reused in a DTO for exact correlation, effect APIs must still require the caller's independent capability/owner validation.

## Integration with tracing

Inspector answers “what is true now”; Phase 6 trace answers “how did we get here”. Add correlation IDs so a graph node can point to trace history without making trace history authoritative.

## Required tests

- self-scoped process cannot inspect unrelated service graph;
- privileged inspector can see authorized cross-service graph;
- sensitive host/security facts are redacted by default;
- stale generation is represented as stale, not merged with current node;
- region blocked by RegionUse reports exact pin;
- region blocked by platform mapping/backing reports exact class;
- process reclaim blocked by ExternalOperation reports closure dependency;
- service replacement relationship preserves N -> N+1 provenance;
- inspector snapshot cannot be used to bypass normal capability checks.

## Exit criteria

Developers can deterministically explain major ownership/reclaim/delegation failures without kernel debugger access, while the inspection plane remains read-only and visibility-scoped.