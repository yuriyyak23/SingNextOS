# P14-3 — Region BORROW/MOVE Edges and Exact Ownership Sequence

## 1. Goal

Extend qualified Job edges to existing `RegionAuthority` BORROW/MOVE semantics without creating a Job-local ownership ledger and without collapsing ordinary SIP ownership/generation transitions.

`RegionAuthority` remains the sole source of truth.

## 2. Core rule

A Region-bearing Job edge describes **transition intent**, not ownership authority.

A `JobRunFrame` MAY cache current opaque Region handles returned by existing owners, but a cached field such as `CurrentOwner` or `CurrentGeneration` is evidence/lookup state only. The authoritative owner/generation is whatever `RegionAuthority` accepts at the linearization point.

## 3. Edge descriptors

### `RegionBorrowEdgeDescriptor`

Contains non-authoritative metadata such as:

```text
RegionSourceRole
ProducerStageId
ConsumerStageId(s)
RequiredUseMode = ReadOnly for the initially qualified contour
Expected schema/element contract
LifetimeScopeId
ClosurePolicyId
```

### `RegionMoveEdgeDescriptor`

Contains:

```text
ProducerStageId
ConsumerStageId
OwnershipTransitionIntents[]
Expected contract roles
SettlementPolicyId
```

`OwnershipTransitionIntents[]` describes the sequence required by ordinary SIP contracts. It does not predeclare successful owner/generation values.

## 4. Preserve ordinary ownership sequence

Consider an ordinary two-stage composition where Stage A returns an ownership-bearing response to the caller and the caller sends that Region as a consuming request to Stage B.

Ordinary sequence:

```text
A/responder owner @ G
    -> RegionAuthority.Transfer(A -> caller)
caller owner @ G+1
    -> RegionAuthority.Transfer(caller -> B)
B owner @ G+2
```

A fused Job MUST perform the semantically equivalent authoritative sequence. It may remove the response envelope and request queue, but it MUST NOT replace both transfers with `A -> B @ G+1` merely because B is the final owner.

If a separately defined SIP contract contour performs a direct service-to-service transfer with no intermediate caller ownership, that contour may have one transition—but that fact must come from the contract/ordinary path, not from Job optimization.

## 5. MOVE terminal rules

After a successful authoritative MOVE:

- the old handle/generation becomes stale according to existing `RegionAuthority` semantics;
- downstream failure MUST NOT synthesize an inverse MOVE unless the ordinary protocol explicitly performs a new authoritative transfer;
- Job cleanup MUST NOT overwrite owner state from cached metadata;
- release/cleanup may close uses/pins, not invent ownership compensation.

This is a non-transactional Job.

## 6. BORROW rules

Initially qualify only read-only BORROW.

Required properties:

- use acquisition through `RegionAuthority`;
- no per-element capability lookup after a valid view is acquired, consistent with existing Region semantics;
- no raw Region backing reference projected as arbitrary CLR authority;
- valid view rematerialization only while the authoritative lease/use remains live;
- lease lifetime dominates all consumers;
- async/parallel extension requires separate gates.

A consumer MAY access elements through the already-qualified view mechanism without repeated capability checks, but cannot extend lease lifetime by retaining a raw reference.

## 7. Lifetime domination

For linear execution:

```text
AcquireUse
  dominates
    first consumer access
  and remains live through
    last consumer access
then
ReleaseUse
```

For future read-only fan-out:

```text
shared lease acquisition
    -> branch A/B/... may create valid views
    -> every dependent branch settles
    -> deterministic join settlement
    -> shared lease release
```

A branch finishing early MUST NOT release a shared lease still required by a sibling.

## 8. Reclaim and generation races

All Region operations are generation-sensitive. The Job path must tolerate races with:

- reclaim;
- owner termination;
- competing transfer;
- use acquisition/release;
- service restart;
- cached binding invalidation.

A stale generation causes the existing owner to reject. The Job MUST NOT retry by guessing the new generation or by updating its cached owner from telemetry.

## 9. Terminal-path matrix

Every qualified Region edge must be tested for:

| Terminal path | Required result |
|---|---|
| success | exact ordinary transfer/use sequence, no leaks |
| exception before Region transition | owner/use unchanged except explicitly committed earlier state |
| exception after committed MOVE | new owner remains authoritative; no implicit inverse MOVE |
| cancel before consumer stage | no consumer admission; preserve already committed ordinary ownership semantics |
| cancel during consumer stage | lease/use closes according to existing lifecycle; no early reclaim |
| service restart | stale generation/session rejects; Region owner remains whatever `RegionAuthority` committed |
| reclaim race | exactly one legal ordering wins; loser sees stale/denied result |
| provider ambiguity | do not reclaim/release ownership until the existing external-operation/visibility rules permit it |
| branch partial completion | shared-read lease remains until last dependent branch settles |
| join failure | same ownership/use settlement rules as defined branch outcomes; no transaction rollback |

## 10. Conflict matrix

The graph verifier/runtime must conservatively reject unsupported conflicts.

Initial permitted forms:

```text
single linear MOVE chain
single linear read-only BORROW consumer
future read-only BORROW fan-out under FG-READONLY-DAG
```

Not admitted here:

```text
MOVE fan-out
concurrent writer + reader
multiple writers
shared mutable aliases
implicit ownership merge
```

Those forms remain FutureGated.

## 11. Differential executable proofs

### MOVE sequence test

Use a Region-producing Stage A and Region-consuming Stage B whose ordinary composition has an intermediate caller ownership state.

Compare ordinary vs fused:

- ordered owner IDs/roles;
- number of `Transfer` events;
- generation after every transfer;
- stale status of old handles;
- protocol/publication state around each transfer.

### Reclaim race

Inject reclaim immediately before and after each transfer/use linearization. Exactly the same set of legal outcomes must be observed as ordinary SIP.

### No inverse MOVE

Stage B throws after successful incoming MOVE. Assert owner remains the ordinary post-transfer owner; Job cleanup does not move back.

### Borrow lifetime

Attempt to access/rematerialize after lease close. Must fail identically to existing Region rules.

## 12. Performance constraint

Removing queue/envelope work must not be reported as "zero security cost". Region transitions/use checks remain and must be counted explicitly.

Benchmarks record:

```text
Region use acquisitions/op
Region transfers/op
Region generation advances/op
Region lock wait
payload bytes copied (should remain zero for qualified BORROW/MOVE data path, excluding unrelated staging)
```

## 13. PR decomposition

1. Region edge descriptors/verifier rules with execution gate OFF;
2. linear BORROW sentry integration;
3. BORROW terminal/race qualification;
4. MOVE transition-sequence model;
5. MOVE differential generation/owner tests;
6. enable gates separately: BORROW first, MOVE second.

## 14. Exit criteria

- no Job-local Region owner ledger exists;
- every MOVE preserves the ordinary authoritative transition sequence;
- BORROW lifetime is proven on all terminal paths;
- stale generation/reclaim races fail closed;
- no implicit inverse MOVE exists;
- BORROW and MOVE gates qualify independently.
