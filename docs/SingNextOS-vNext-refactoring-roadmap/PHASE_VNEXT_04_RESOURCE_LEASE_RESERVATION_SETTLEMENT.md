# P04 — Resource leases, reservation, binding and settlement

## Goal

Turn resource authority into a consumable linearized lease without yet integrating SIP or providers.

## State machine

```text
Created
  -> Reserved
  -> Bound
  -> Consuming
  -> Settling
  -> Released
               -> Exhausted
        -> Quarantined
```

Exact names may differ, but semantic distinctions are mandatory.

## Proposed objects

```text
ResourceLeaseHandle
ResourceReservationHandleV2
ResourceUsageSettlement
ResourceLeaseSnapshot // evidence only
```

## Rules

1. One lease generation cannot bind to two concurrent operations.
2. Split creates new child leases and invalidates/decrements the parent delegable envelope atomically.
3. Before execution starts, unused reservation may be fully released.
4. After consumption starts, refund is based only on authoritative settlement rules.
5. Unknown provider consumption => `Quarantined`, not automatic refund.
6. Accounting ledger is charged/released through one exact correlation with lease settlement.
7. Resource release does not release Region uses or external effects.

## Initial resource contour

Implement **one** executable semantic resource first:

```text
ComputeTime nanoseconds per explicit lease
```

Period/replenishment is deferred to P11. This keeps P04 about linearity and conservation.

## Required races

- reserve vs revoke;
- bind vs transfer;
- settle vs cancel;
- double settlement;
- owner restart during reservation;
- stale lease replay;
- accounting underflow/overflow prevention.

## Exit criteria

`FG-VNX-RESOURCE-LEASE` reaches `RuntimeEnforced` for host/model ComputeTime only.
