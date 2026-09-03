# Phase 6 — Execution lifecycle, scheduler interaction, events and boot qualification

## Status

**In progress after real domain binding.** Phase 3 already provides synchronous,
definitive neutral `Start / Park / Resume` transitions and publishes local process
state only after exact provider success. Phase 5 already provides the first
generation-bound `KernelEventEndpoint` use through IRQ delivery.

The first Phase 6 slice closes the remaining execution-attachment and causal
identity gaps: an external domain may be attached or explicitly detached only
while the process is `Created` or `Admitted`, and the v2 platform subject contains
the exact `DomainId + ProcessHandle`. Scheduler-policy admission, reuse of the
event primitive by another asynchronous subsystem, and AOT/image/ISE
qualification remain open. External qualification can proceed independently and
corresponds to `EXT-HCPU-001`.

## Goal

Connect Sing process/domain lifecycle to HybridCPU execution admission and event mechanisms without making HybridCPU lane topology or exact-cycle scheduling part of the Sing ABI.

## Execution lifecycle

Phase 3 established the two-layer transition:

```text
Sing validates local process/domain/capability state
  -> provider requests neutral execution transition
  -> HybridCPU runtime admits/completes transition
  -> Sing publishes Running/Parked/etc.
```

If the platform cannot start/park/resume the domain, Sing must not publish the target state merely because the request was issued.

### Completed slice A — execution attachment integrity

The Phase 6 audit found two paths around that transition even though the normal
bind-before-start path was already correct:

```text
unbound local process becomes Running
  -> late BindPlatformDomain materializes external Ready

bound process becomes Running or Parked
  -> explicit RevokePlatformDomain closes external execution authority
  -> local process remains Running or Parked
```

Both paths are now rejected before a provider call. For one exact process
generation, attachment rules are:

```text
Created | Admitted -> bind is state-eligible after contract/provider checks
Created | Admitted -> explicit detach is state-eligible after dependents close
Running | Parked   -> bind/detach is denied without local state mutation
Exiting            -> only the tracked process-teardown path may close the domain
```

Therefore an unbound process that has already started remains local-only for that
generation. A bound process that has started retains its exact locally tracked
binding or quarantine record until process teardown closes dependent authority,
obtains provider-domain closure proof and only then reclaims local process/domain
state. This does not assert that provider-side authority remains live after an
external revocation. A failed provider start leaves the process `Admitted`, so
its never-started binding can still be explicitly closed.

`PlatformDomainIdentity` v2 is `(DomainId, ProcessHandle)`, not
`(DomainId, ProcessGeneration)`. Process generations are monotonic per
`ProcessId`, so the earlier shape collided for two live processes in the same
domain that both had generation `1`. Such siblings now receive independent
provider leases, and one sibling cannot present another sibling's local binding
to close its provider authority. `ProcessHandle`, local binding identity and
provider lease identity remain separate typed values. `NeutralDomains v2` is the
SingPlus platform/provider contract version, not a new HybridCPU runtime or ISE
ABI. A provider that explicitly advertises only v1 is rejected before any
authority call; a bare legacy feature bit remains classified as v1 rather than
being silently upgraded.

A successful provider bind is publishable only when the returned lease has a
non-zero provider lease ID, a non-zero provider generation and the exact requested
subject. If best-effort cleanup of malformed bind output fails, the bridge keeps
an internal quarantined local record and RuntimeKernel tracks it without returning
a caller-visible binding. The process cannot start locally or reclaim while that
unresolved provider lease remains.

A malformed/stale successful execution result, and a provider `Stale`, `Revoked`,
`WrongDomain` or `Faulted` failure, quarantines the binding: it cannot authorize
another effect. After already-materialized dependents have reached their own
verified closure, exact teardown can still request closure of the retained
provider lease. If quarantine prevents proving dependent closure, teardown stays
pinned rather than assuming it. `Revoked` returned to the bridge by an effect
request alone is not closure evidence. Only a successful exact provider
`RevokeDomain` confirms external closure and permits local binding release. A
cleanup/close failure encountered by tracked process teardown pins the process
in `Exiting`; local reclaim remains forbidden.

The HybridCPU adapter keeps an exact subject reservation when its private neutral
lease reports `Revoked` during a transition. Its later exact `RevokeDomain` call
still invokes `Close` on the private neutral lease; only `Closed` or the exact
already-`Revoked` close result releases the reservation. A transition failure
cannot substitute for that close call.

No asynchronous execution-completion token is introduced by this slice. The
current HybridCPU neutral transition is synchronous and definitive. If a future
provider makes it asynchronous, it must compose with the existing
`PlatformOperationIdentity` / completion contract and reject stale completion
generations; parsing or receipt possession alone must not publish process state.

## Scheduling contract

Expose intent, not implementation topology.

Good inputs:

```text
ExecutionBudget
PriorityClass
LatencyHint
ThroughputHint
DeadlineProfile (future/optional)
AffinityClass (only if semantically justified)
```

Bad inputs:

```text
lane 0..7 placement
VLIW slot mask
SMT virtual-thread ID
exact physical functional unit selection
```

HybridCPU runtime remains authoritative for legality, lane materialization and scheduling-budget enforcement.

## Event/wait primitive

Introduce or standardize one minimal kernel/runtime event/completion abstraction suitable for:

- process park/wakeup;
- timer completion;
- IRQ delivery;
- DMA completion;
- accelerator completion;
- virtualization traps/events;
- platform domain transition completion.

High-level source APIs remain `Task`/`ValueTask`, cancellation and typed SIP events. The event primitive is not a POSIX signal subsystem and does not expose hardware opcodes such as WFE/SEV.

## Cancellation

Compose with Track A rather than replacing it:

- SIP call cancellation closes/cancels protocol work at the service/runtime boundary;
- platform operation cancellation requests external closure;
- caller-visible cancellation is published only with a well-defined ownership state;
- cancelled platform work must still drain/revoke mappings before buffer reuse.

## Boot/AOT/ISE qualification

Do not redesign SingNextOS around the external toolchain. Treat the toolchain as a black-box qualification lane:

```text
build Sing kernel/boot assembly
  -> local admission proof
  -> external HybridCPU AOT/image toolchain
  -> HybridCPU image
  -> ISE execution
```

Record:

- exact SingNextOS commit;
- exact HybridCPU/toolchain version;
- admission proof digest;
- generated image digest;
- ISE acceptance/result;
- whether failure is local, toolchain, loader or runtime admission.

This should become a reproducible integration artifact when the external toolchain is available, but it must not block host-side architecture tests.

## Real-time claims

Do not infer hard real-time from typed lanes, replay or scheduling budgets.

A future RT profile needs explicit evidence for:

- bounded execution budget;
- cache/memory latency envelope;
- interrupt/timer latency;
- SMT interference;
- DMA completion bounds;
- WCET/schedulability analysis;
- overload behavior.

Until then expose only supported budget/priority semantics.

## Completed slice-A tests

- provider start/park/resume failures leave the prior local process state unchanged;
- late bind of a local `Running` or `Parked` process is rejected before provider bind;
- explicit detach of a bound `Running` or `Parked` process is rejected before provider revoke;
- failed start may close the still-`Admitted` binding without publishing `Running`;
- stale process/binding generations and forged binding identity cannot detach authority;
- a same-domain, same-generation sibling cannot close another process's binding;
- same-domain peers can hold distinct bindings because the subject includes `ProcessId`;
- zero provider lease IDs/generations are never published as local authority;
- malformed success is quarantined and permits only exact closure/teardown;
- provider `Revoked` quarantines authority until exact domain close succeeds;
- provider `Faulted` quarantines authority and prevents a second transition call;
- a faulted pre-start close prevents subsequent execution on ambiguous authority;
- non-admission feature classes cannot materialize a domain, and
  `RuntimeAdmission` cannot publish executable lifecycle state;
- real HybridCPU external-close observation retains the subject reservation until
  exact provider close acknowledgement;
- failed malformed-bind compensation stays internally tracked and pins reclaim;
- a later successful exact teardown close can recover that internal quarantine
  before publishing local exit/reclaim;
- post-start provider-close failure pins teardown in `Exiting` and forbids local reclaim.

## Remaining Phase-6 tests

- stale lifecycle completion cannot transition a recycled process;
- park waits for platform completion when required;
- scheduler request contains no lane IDs/raw opcodes;
- event routed after process generation change is rejected;
- cancellation of a platform operation cannot return an owned buffer before Phase 2/4 closure;
- external toolchain qualification records versioned evidence without changing native API semantics.

## Next implementation pool and external boundaries

The next pool is the first minimal scheduler-policy slice: semantic execution
budget, priority and latency/throughput intent tied to the exact local v2
platform-domain binding; the provider lease remains bridge-private. No lane,
slot, SMT or raw opcode becomes ABI. The host path may be classified only as
`ModelOnly`. The current HybridCPU neutral runtime exports synchronous lifecycle
transitions but no stable scheduler-policy API, so real enforcement remains
`ExternalBlocked` under the remaining part of `EXT-HCPU-003`.

Real timer binding remains `ExternalBlocked` under `EXT-HCPU-002`. Reproducible
AOT/image/ISE qualification remains `ExternalBlocked` under `EXT-HCPU-001`.
Neither boundary is replaced with a locally fabricated success path in this
slice.

## Acceptance criteria

Phase 6 is complete when process lifecycle state is causally tied to neutral HybridCPU execution lifecycle, one reusable event/completion primitive handles asynchronous platform effects, and the AOT/image/ISE path is either reproducibly qualified or explicitly remains `ExternalBlocked` with no fabricated fallback claim.

## Do not do

- no exact-cycle scheduling ABI;
- no physical lane allocation in Sing kernel;
- no POSIX signal semantics as a kernel foundation;
- no toolchain-specific types in native app/service contracts;
- no claim of hard real-time without timing evidence.
