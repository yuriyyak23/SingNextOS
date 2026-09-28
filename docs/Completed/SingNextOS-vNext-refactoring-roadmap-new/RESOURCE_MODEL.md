# Corrected resource model

## Semantic families

### Time resources

Canonical example: `ComputeTimeNs`.

Operations: narrow amount/window, reserve, consume, settle. A time amount from one semantic execution class is not automatically fungible with another provider's time.

### Throughput resources

Examples: `DmaBytesPerWindow`, `NetworkTxBytesPerWindow`, `FabricBytesPerWindow`.

Operations require explicit `(bytes, window)` semantics and overflow-safe rate/window arithmetic. Throughput reservations do not imply queue occupancy or completion latency guarantees.

### Occupancy resources

Examples: `DeviceLocalMemoryBytes`, `QueueSlots`, `InflightOperations`, `GuestMemoryBytes`.

These are held for a lifetime and released on authoritative closure. They are not replenished like time-period budgets unless separately specified.

## Forbidden dimensional operations

The common algebra may compare only same-family, same-unit, same-semantic-class values. It must reject:

```text
CPU ns + GPU ns
CPU ns + bytes/sec
queue slots + bytes
energy + time
```

unless a separately versioned policy defines a conversion that is explicitly non-authoritative for admission.

## Accounting, permission, reservation, guarantee and evidence

| Object | Meaning | Owner | Can authorize effect? | Can reserve/consume? |
|---|---|---|---:|---:|
| Budget account/snapshot | configured capacity/accounting | `ResourceBudgetAuthority` | no | account owner only |
| Resource-use grant | permission to spend a bounded resource envelope | `CapabilityAuthority` | no semantic effect by itself | permits budget admission when combined with ledger state |
| Reservation/lease | exact quantitative committed capacity | `ResourceBudgetAuthority` | no effect by itself | yes |
| Provider request/binding | platform-specific admission input | provider bridge | no | provider-side only |
| Usage receipt | evidence of observed usage | provider/runtime | no | no |
| Settlement | authoritative charge/refund/quarantine transition | `ResourceBudgetAuthority` | no | closes local quantitative state |
| Guarantee qualification | claim that minimum service is assured | qualification evidence | no | no |

## Minimal lease state machine

```text
Prepared
 -> Reserved
 -> Bound
 -> Consuming
 -> Settling
 -> Released

Any state after possible external consumption
 -> Quarantined
 -> Reconciled -> Settling/Released
```

`Cancelled` is an event, not proof of no consumption. Before submit it may cause full release; after possible submit it follows reconciliation policy.

## Conservation obligation

For one budget lineage and one resource dimension:

```text
available + reserved + conservatively_charged + irreversibly_consumed
    <= admitted parent limit
```

Child grants do not clone quantitative capacity. A delegated capability only constrains who may request reservations; capacity is committed exactly once in `ResourceBudgetAuthority`.
