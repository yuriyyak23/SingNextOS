# Phase 2 — Revocation, teardown and completion lifecycle

## Status

**Complete for the currently implemented authority classes.**

Phase 2 now has two composed vertical slices:

1. completion-backed owned-region mapping revocation;
2. process exit/fault orchestration that closes local channels and authority first, drains all process-owned platform mappings, closes the platform domain binding, and only then performs local region/domain reclaim and publishes `Exited` / `Faulted`.

This phase does **not** claim DMA, accelerator, VM, display or real HybridCPU teardown. Those authority classes do not exist yet in the current runtime and must reuse this lifecycle unchanged when their later roadmap phases add them.

## Completed foundation from Phase 1

Phase 1 established the prerequisites this phase consumes:

- typed/versioned provider discovery;
- opaque `PlatformOperationId` / operation generations;
- completion states and identity-validated receipts;
- explicit memory-visibility vocabulary.

## Completed slice 1 — completion-backed region mapping revocation

The bridge tracks two independent dimensions for every platform region mapping:

```text
LocalAuthorizationRevoked = false | true

PlatformClosure:
  Active
  Draining
  Closed
  Faulted
```

This deliberately allows the security-critical state:

```text
LocalAuthorizationRevoked = true
PlatformClosure = Draining
LocalReservationReleased = false
```

A dead local capability therefore never implies that external authority has stopped touching the region.

`PlatformRegionMappingLifecycle` exposes only semantic local/closure state. It contains no provider lease, physical address, VMX/VMCS state, lane/opcode or hardware descriptor. `LocalReclaimAllowed` becomes true only when:

```text
PlatformClosure == Closed
&& LocalReservationReleased == true
```

### Narrow provider revocation contract

`IPlatformRegionRevocationProvider` exposes one mapping-specific begin operation:

```text
BeginRegionMappingRevocation(mapping lease, policy)
  -> PlatformRegionRevocationTicket
```

The ticket binds provider mapping ID/generation to an opaque `PlatformOperationIdentity`. The bridge validates the exact mapping/domain generations before accepting the ticket. A legacy provider that only returns synchronous revoke success remains non-reclaimable because it cannot provide a completion-backed `Closed` receipt.

### Reclaim gate

A region reservation is released only after all of the following succeed:

1. exact local `PlatformRegionMapping` identity;
2. exact provider mapping/generation in the revocation ticket;
3. exact opaque operation/domain generation in the completion receipt;
4. receipt state exactly `Closed`;
5. exact local platform binding generation and subject;
6. exact region generation and owner;
7. local reservation release;
8. bridge acknowledgement of `LocalReservationReleased`.

`Completed`, `Cancelled`, `Faulted`, stale/wrong-domain/malformed receipts and legacy synchronous success are never reclaim proof.

## Completed slice 2 — process exit / fault completion orchestration

`RuntimeKernel.TerminateProcess()` and `RuntimeKernel.FaultProcess()` no longer require callers to pre-revoke platform authority. They now begin the same explicit non-blocking teardown lifecycle.

The observable process-level semantic phases are:

```text
ProcessState.Exiting
  + ProcessTeardownPhase.LocalExitStarted
      -> ProcessTeardownPhase.PlatformDraining
      -> ProcessTeardownPhase.PlatformClosed
      -> ProcessState.Exited | ProcessState.Faulted
```

Provider/closure failure instead produces:

```text
ProcessState.Exiting
  + ProcessTeardownPhase.PlatformFaulted
  + LocalReclaimCompleted = false
```

A provider fault therefore does **not** publish a fully exited/faulted process and does not release pinned regions.

`ProcessTeardownSnapshot` contains only local semantic evidence:

- exact `ProcessHandle` generation;
- requested local terminal state (`Exited` or `Faulted`);
- teardown phase;
- whether exact process channels were closed;
- whether local authorization was revoked;
- count of platform mappings still pending;
- whether the platform domain binding is closed;
- whether local reclaim completed;
- a semantic `KernelError` blocking reason when fault-contained.

It contains no provider lease/operation IDs and is not a capability.

### Required ordering

The begin path is intentionally ordered:

```text
1. mark process Exiting
2. close exact process-owned channels
   -> ResponseRegistry cancels pending waiters
   -> previously committed responses remain committed
3. mark/revoke process-held local capability authority
4. begin/observe all tracked process platform-mapping closures
5. require every mapping to pass its existing exact Closed receipt + local-generation reclaim gate
6. revoke the process platform-domain binding
7. return loans borrowed by the exact process generation
8. reclaim regions owned by the exact process generation
9. remove the process from domain membership
10. only for the final domain member, perform residual domain-wide capability/loan/region/channel cleanup
11. publish Exited or Faulted and retire the exact process generation
```

No unbounded provider drain is hidden inside a syscall. A synchronous host backend can complete the whole sequence in the initial call. A deferred backend leaves the process live in `Exiting`; `ObserveProcessTeardown()` advances it later.

### No-new-effects gate

Once a process enters `Exiting`, it cannot mint/delegate new capabilities, allocate/transfer/release regions, create new channels, bind a new platform domain, or create new platform mappings. Existing channels were already closed before platform drain began, so send/receive/response operations fail through stale endpoint generations.

Authority-reducing and observation paths needed by teardown remain available internally.

### Process-generation-specific cleanup

Local cleanup no longer relies only on domain-wide reclaim. `RegionAuthority` can now:

- return loans borrowed by one exact `RegionOwner` (`DomainId + process generation`);
- reclaim regions owned by one exact `RegionOwner`;
- refuse that reclaim if any owned region is still platform-reserved.

This lets one process leave a multi-process domain without destroying sibling process regions, capabilities or channels. Domain-wide cleanup is deferred until the final member exits.

## Fault containment

For a provider closure fault or another hard external-closure error:

- process state remains `Exiting`;
- exact process channels are already closed;
- process-held local capability authority is already dead;
- affected regions remain pinned/reserved;
- `LocalReclaimCompleted` remains false;
- `QueryProcessTeardown()` exposes a semantic fault-contained snapshot;
- no rollback restores local authority;
- a future platform-reset contract would still need stronger closure proof before reclaim.

## Composed generations

The completed Phase-2 paths compare independent epochs rather than inventing one global generation:

```text
process generation
capability revocation state
region generation / RegionOwner process generation
local platform binding generation
local platform mapping generation
provider mapping/domain lease generation
operation generation
```

Any mismatch at the relevant boundary invalidates the operation or reclaim attempt.

## Tests

Phase-2 coverage now proves:

- capability revoke kills local authority immediately while deferred mapping closure stays `Draining` and the region remains pinned;
- valid `Closed` receipt allows release only after exact local binding/region revalidation;
- stale/wrong-domain/malformed completion cannot release a reservation;
- `Faulted` completion remains observable and non-reclaimable;
- duplicate valid `Closed` observation is idempotent;
- legacy synchronous revoke without a receipt cannot authorize reclaim;
- process termination closes/cancels pending SIP response waiters before deferred platform drain completes;
- a process remains `Exiting`, not `Exited` / `Faulted`, while any mapping is still draining;
- `FaultProcess` publishes terminal `Faulted` only after verified platform closure and local cleanup;
- a response committed before teardown remains published even when later platform closure faults;
- after `Exiting`, authority-producing region/capability/channel/platform APIs fail closed;
- one process exiting a shared domain reclaims only its exact process-generation resources while sibling process authority remains live;
- final-domain-member cleanup remains domain-wide only after membership reaches zero.

## Acceptance criteria

Phase 2 is complete for current code when:

- local revocation / `Exiting` immediately prevents all new process authority uses;
- external platform closure is represented independently as `Draining`, `Closed` or fault-contained;
- local region reclaim is mechanically impossible before exact verified `Closed` evidence;
- process terminal state is mechanically impossible before all current platform mappings close, the platform domain binding closes, and local cleanup succeeds;
- SIP waiter cancellation precedes platform drain waiting/observation;
- committed responses are not rolled back by later teardown failure;
- multi-process domains do not suffer premature domain-wide cleanup.

The implementation now satisfies these criteria for the platform domain + owned-region mapping authority classes that currently exist.

## Reuse requirement for later phases

Later DMA, accelerator, virtualization and display work must plug into this same lifecycle:

```text
local authority dead / process Exiting
  -> external authority Draining
  -> exact completion receipt Closed
  -> exact local generation revalidation
  -> local reclaim
```

They must not create parallel timeout-based or synchronous-success reclaim rules.

## Do not do

- do not restore revoked local authority because external revoke failed;
- do not free/reuse memory after a timeout unless a platform contract proves reset/revocation;
- do not model provider completion as a normal SIP response capability;
- do not assume synchronous device shutdown;
- do not treat legacy synchronous provider success as `Closed` evidence;
- do not flatten process/capability/region/binding/provider/operation generations into one epoch;
- no HybridCPU binding or DMA in Phase 2.

## Next roadmap phase

Proceed to **Phase 3 — real neutral HybridCPU domain binding**. The Phase-2 teardown lifecycle is now the required security boundary that any real backend must plug into rather than bypass.
