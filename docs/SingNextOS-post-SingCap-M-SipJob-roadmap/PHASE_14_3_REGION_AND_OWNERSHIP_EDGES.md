# P14-3 — Region BORROW/MOVE Edges and Failure Settlement

## Goal

Extend fused linear Jobs from bounded values to the existing `RegionAuthority` ownership/use model without inventing Job-local memory authority.

## Non-negotiable rules

- `RegionAuthority` remains the only owner of RegionId/generation/owner/use truth.
- A fused MOVE still changes logical owner/generation exactly as an ordinary SIP MOVE would; “same CLR” is not permission to skip ownership transition.
- A fused BORROW uses the qualified Region borrow/use mechanism; shared address space does not imply shared authority.
- Failure after MOVE is not an implicit inverse transfer.

## Edge semantics

### BORROW edge

```text
producer/current owner
  -> acquire exact read/use/borrow authority
  -> create operation-specific projection for consumer stage
  -> execute stage
  -> settle/return/revoke borrow according to lifetime
```

### MOVE edge

```text
source owner + RegionGeneration G
  -> validate transfer
  -> RegionAuthority.Transfer
  -> target owner + new RegionGeneration G+1
  -> invalidate source ownership wrapper
  -> update trusted Job edge slot with new handle
```

The Job edge slot stores the current handle/evidence; it does not own the Region.

## Ownership graph verifier

For every Region symbol in the plan, statically/model-check all terminal paths available in this phase:

```text
success
stage throws before consuming edge
stage throws after MOVE
caller cancellation at permitted boundary
session/service stale before next stage admission
```

Properties:

```text
exactly one mutable owner after MOVE
no double consume
no use after MOVE
no missing final owner
borrow lifetime closes exactly once
reclaim cannot pass live use/borrow
```

## Settlement descriptors

The plan may contain generated settlement metadata describing allowed existing authority operations, e.g. “return ownership to final caller on successful exit” or “target stage remains owner on failure and response reports failure”. It MUST NOT encode magical rollback.

## Span rule

After successful borrow/use acquisition, stage code may operate on ordinary `Span<T>`/`ReadOnlySpan<T>` according to P07. No capability/Region table lookup is inserted into each element access. A stage must materialize the span once per lexical segment where practical.

## Feature gates

P14-3 qualifies only linear BORROW/MOVE under `FG-REGION-BORROW` / `FG-REGION-MOVE`. Read-only branch fan-out remains `FG-READONLY-DAG` and is deferred to P14-5. Shared mutable DAG remains FutureGated beyond P14.

## Primary paths

```text
src/Runtime/SingPlus.Runtime/Regions/RegionAuthority.cs
src/Runtime/SingPlus.Runtime/Channels/ChannelRegistry.cs (semantic reference/shared primitives)
src/Sip/SingPlus.Sip/Regions/OwnedBuffer.cs
src/Sip/SingPlus.Sip/Regions/BorrowLease.cs
src/Sip/SingPlus.Sip/Regions/OwnedRegion.cs
new internal Jobs edge/settlement code
```

## PR slices

- P14-3.1: generated Region edge descriptors and linear verifier.
- P14-3.2: fused BORROW edge using existing Region authority.
- P14-3.3: fused MOVE edge using existing transfer primitive.
- P14-3.4: terminal-path settlement engine with no authority duplication.
- P14-3.5: differential normal-vs-fused ownership state tests.

## Tests

```text
BORROW success/exception/cancel settlement
MOVE success
fault before MOVE => source remains owner
fault after MOVE => defined target/final owner, no fake rollback
double MOVE race => one winner
session close/revoke around Region transition
stale RegionGeneration/BorrowGeneration/MutationEpoch
reclaim blocked by active use
multi-region deterministic ordering compatibility with P07
no per-element authority lookup regression
```

## Performance gate

Payload sweep (at least 64 B, 4 KiB, 64 KiB, 1 MiB) comparing normal SIP vs fused BORROW/MOVE. Report metadata latency separately from useful buffer processing.

## Exit criteria

Linear fused Region edges preserve the same ownership/use truth as normal SIP and every terminal path has executable-tested settlement semantics.
