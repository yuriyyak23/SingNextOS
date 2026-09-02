# Phase 6 — Events, completions and teardown

## Goal

Generalize the lifecycle discipline already proven in SIP response teardown and current process-exit orchestration into a common, policy-neutral completion model.

## Unified event/completion substrate

Use one small kernel/runtime concept for asynchronous wakeup/completion where practical. It should be reusable by:

- channel/request completion;
- timers;
- IRQ delivery;
- DMA completion;
- accelerator jobs;
- virtualization exits/events;
- display/surface release fences.

Service-specific semantics stay in the SIP/service layer.

## Common operation identity

Externally backed operations should have an opaque local operation identity/generation and a completion state such as:

```text
Staged
Pending
Draining
Completed
Cancelled
Closed
Faulted
```

The exact type may reuse the Platform Contract vNext work. Do not introduce duplicate lifecycle systems for each service family.

## Causal teardown invariant

Local authority revocation stops **new** effects immediately, but local resource reclaim waits until old external effects are known closed.

```text
Active
-> LocallyRevoked / Exiting
-> PlatformDraining
-> PlatformClosed
-> LocalReclaimAllowed
```

A provider failure during revoke does not restore local authority; it leaves the affected resource pinned/quarantined until closure can be established or the enclosing domain is destroyed safely.

## Process/component termination

Termination should be orchestrated, not implemented as dictionary removal:

```text
stop accepting sessions
-> close/cancel SIP work
-> revoke local capabilities
-> begin platform drain
-> drain DMA/IRQ/compute/VM/display operations
-> revoke mappings/leases
-> close platform domain
-> return/reclaim ownership
-> finalize process/component state
```

## Completion races

Required semantic rules:

- a committed/published result remains committed if the peer dies afterward;
- cancellation before commit prevents publication/ownership transfer unless the contract defines otherwise;
- duplicate completion is rejected/idempotently ignored according to operation semantics;
- stale completion generations cannot affect recycled resources;
- terminal state cannot transition back to active.

## Tests

Use deterministic and fault-injection providers to cover:

- completion vs cancellation races;
- revoke vs completion races;
- provider fault while draining;
- process death with pending IRQ/DMA/compute work;
- stale completion after process-generation reuse;
- timer/IRQ/DMA sharing the same primitive without service-specific kernel syscalls.

## Acceptance criteria

At least two different asynchronous subsystems beyond ordinary SIP responses must use the same lifecycle/completion substrate and pass teardown fault injection.
