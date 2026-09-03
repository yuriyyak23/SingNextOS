# Phase 6 — Execution lifecycle, scheduler interaction, events and boot qualification

## Status

**In progress after real domain binding.** Phase 3 already provides synchronous,
definitive neutral `Start / Park / Resume` transitions and publishes local process
state only after exact provider success. Phase 5 already provides the first
generation-bound `KernelEventEndpoint` use through IRQ delivery.

The first Phase 6 slice closes the remaining execution-attachment and causal
identity gaps: an external domain may be attached or explicitly detached only
while the process is `Created` or `Admitted`, and the v2 platform subject contains
the exact `DomainId + ProcessHandle`.

The second slice adds the local `ExecutionPolicy` v1 contract. It carries only a
semantic `ExecutionBudget`, `PriorityClass`, `LatencyHint` and `ThroughputHint`,
is configured against the exact live v2 local platform-domain binding, and
returns a local `PlatformExecutionPolicyRegistration` only after provider
success. The host provider advertises this feature only as `ModelOnly`.
`HybridCpuPlatformAuthorityProvider` reports it unavailable because the current
neutral runtime has no stable scheduler-policy interface; real admission and
enforcement therefore remain `ExternalBlocked` under `EXT-HCPU-003`.

The third slice applies the same process-generation-bound `KernelEventEndpoint`
mailbox to exact DMA completion observation. The new overload reserves an
invisible endpoint slot before provider observation and commits one local
`Completion` event only after exact v4 `Completed` evidence validates. Pending,
denied, faulted, malformed or stale outcomes publish nothing. The event remains
a wakeup, not completion authority, CPU-visibility evidence or reclaim proof.

A cancellable asynchronous waiter over the endpoint and AOT/image/ISE
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

## Completed slice B — minimal scheduler-policy contract

`ExecutionPolicy` v1 exposes intent, not implementation topology:

```text
ExecutionBudget(
  TimeSpan MaximumExecutionTime,
  TimeSpan ReplenishmentPeriod)
PriorityClass
LatencyHint
ThroughputHint
```

`MaximumExecutionTime` is aggregate provider-accounted execution time within a
positive `ReplenishmentPeriod`; it is not a wall-clock deadline or a single-lane
utilization fraction. It may therefore exceed the replenishment period when a
provider accounts parallel execution contexts. Both durations must be positive,
but their ratio establishes no placement, concurrency or real-time guarantee.
Latency and throughput are independent hints; their combination remains subject
to provider admission rather than becoming a topology assumption in the ABI.

`RuntimeKernel.ConfigurePlatformExecutionPolicy(...)` validates the exact live
process generation and the caller-supplied local `PlatformDomainBinding` before
the privileged bridge resolves its private provider lease. A successful result
returns:

```text
PlatformExecutionPolicyRegistration(
  exact local PlatformDomainBinding,
  exact requested policy,
  exact feature descriptor)
```

The registration is a record of accepted SingNextOS policy intent. It is not a
capability, provider grant, completion receipt, scheduler-quality result or
evidence that budget enforcement occurred. Provider lease identity remains
bridge-private, and no registration is published when local validation, feature
discovery, the provider call or provider-result validation fails.

Policy is immutable for the lifetime of one binding and must be configured
before execution starts. Repeating the exact accepted request is idempotent and
does not call the provider again; attempting to replace it, or configuring it
after the process becomes `Running`, `Parked` or `Exiting`, fails before another
provider policy call.

The contract intentionally has no fields for:

```text
lane 0..7 placement
VLIW slot mask
SMT virtual-thread ID
exact physical functional unit selection
```

The deterministic host provider exercises the contract and its negative
boundaries as `ExecutionPolicy` v1 / `ModelOnly`. The HybridCPU provider does not
fabricate an implementation from lane, slot, SMT or opcode internals and reports
the family unavailable. HybridCPU remains authoritative for legality, physical
placement and any future scheduling-budget enforcement.

## Completed slice C — second asynchronous event source

The minimal kernel/runtime event abstraction is now used by two distinct
platform producers:

| Producer | Delivery gate | Classification |
|---|---|---|
| IRQ | exact provider delivery observation plus exact provider sequence completion | HybridCPU neutral binding where advertised |
| DMA completion | exact v4 provider `Completed` observation for the tracked submission | local/model projection only; no executable HybridCPU DMA claim |

`KernelEventRegistry` separates an invisible staged reservation from committed
consumer-visible delivery. An endpoint still admits only one staged or committed
event. A source reserves that slot, completes its source-specific validation and
then either commits the exact event or rolls the reservation back. This prevents
an occupied endpoint from consuming a one-shot DMA completion observation and
prevents IRQ/DMA failure paths from exposing a placeholder event.

The bridge also admits only one in-flight provider completion observation for
one exact DMA submission. A concurrent observer through another endpoint is
rejected as draining before a second provider call; after the first result is
settled, normal pending retry or completed replay rules apply.

The public DMA shape is only an overload of the existing completion observation:

```text
ObservePlatformDmaCompletion(
  exact ProcessHandle,
  exact PlatformDmaSubmission,
  exact KernelEventEndpoint)
    -> exact PlatformDmaCompletionEvidence
       + one committed KernelEventClass.Completion notification
```

The endpoint belongs to the exact `ProcessHandle` generation. The event source
uses only the local operation ID/generation; provider submission/grant tokens do
not enter the event. A stale/recycled process, stale/closed/foreign endpoint, or
forged operation/grant/cycle identity is rejected before provider observation.
At most one event is committed for one exact completion proof.

The event means only "exact DMA completion was observed." It does not mean that
device-written bytes are CPU-visible. `PlatformDmaCompletionEvidence` remains the
typed input to the existing direction-aware post-completion step; write-capable
DMA still requires a fresh acquire before the submission pin is released. Event
consumption is neither required for nor sufficient to close a grant, mapping,
device or domain, and it cannot authorize region transfer or reclaim.

The same minimal abstraction remains suitable for later:

- process park/wakeup;
- timer completion;
- accelerator completion;
- virtualization traps/events;
- platform domain transition completion.

High-level source APIs remain `Task`/`ValueTask`, cancellation and typed SIP events. The event primitive is not a POSIX signal subsystem and does not expose hardware opcodes such as WFE/SEV.

## Cancellation

Endpoint cancellation and external-operation cancellation remain separate.
Closing a `KernelEventEndpoint` cancels unread/future local notification only;
it does not cancel, complete or close a DMA submission. If the endpoint is full
or closed, provider completion observation is not started through the event
overload. A close that races an already staged provider observation returns
`PlatformBindingDraining`; it can be retried after that exact publication either
commits or rolls back. If the process is already `Exiting`, the event overload is
rejected and the existing endpoint-free completion/post-visibility calls remain
available solely to drain the already-authorized operation.

This composes with Track A rather than replacing it:

- SIP call cancellation closes/cancels protocol work at the service/runtime boundary;
- platform operation cancellation requests external closure;
- caller-visible cancellation is published only with a well-defined ownership state;
- cancelled platform work must still drain/revoke mappings before buffer reuse.

No neutral HybridCPU DMA cancellation/closure receipt exists today, so this
slice does not fabricate `CancelDma` success. Completion, required visibility,
DMA-grant close, mapping/device/domain close and only then local reclaim remain
mandatory after notification cancellation.

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

## Completed slice-B tests

- a valid budget/priority/latency/throughput request against the exact live v2
  binding returns the exact local `PlatformExecutionPolicyRegistration` only
  after host provider success;
- invalid policy values, a stale process generation, and a stale or forged local
  binding are rejected before the provider policy call;
- provider denial, stale/revoked/wrong-domain/faulted failure and malformed
  success do not publish a local registration; ambiguous outcomes quarantine
  the binding until its exact domain close;
- registration identity is tied to the exact process-scoped binding; a
  same-domain sibling cannot configure through it, and a closed old binding
  cannot be reused after a fresh binding is created;
- the first exact policy is immutable, an exact repeat is idempotent, and
  `Running`, `Parked` or `Exiting` processes cannot configure policy;
- exact domain-close failure after policy quarantine pins the process in
  `Exiting` without publishing local reclaim;
- feature contract/version/availability checks fail closed, the host advertises
  only `ModelOnly`, and the HybridCPU provider reports policy unavailable;
- public scheduler-policy contracts contain no lane, slot, SMT, physical-unit,
  raw opcode, VMCS or provider-token authority.

## Completed slice-C tests

- `Pending` DMA completion and ordinary provider denial roll back an invisible
  reservation and leave the endpoint empty; a later exact `Completed` retry
  commits one generation-bound event;
- provider fault, stale/revoked/wrong-domain completion lifetime and malformed
  success publish no event and retain the existing fault pin;
- stale process generation, stale/closed/foreign endpoint, and forged operation,
  grant generation or visibility cycle fail before provider observation;
- a full endpoint backpressures a second DMA completion before provider
  observation, then delivers it exactly once after the first event is consumed;
- an in-flight reservation rejects concurrent observers of one exact submission
  before a second provider call, publishes at most once, and keeps a racing
  endpoint close draining until the staged publication resolves;
- the IRQ source now also commits only after exact provider delivery completion,
  and failed completion exposes no event before a safe retry;
- consuming the DMA notification does not permit grant closure, region transfer
  or CPU access before exact post-completion visibility;
- closing the endpoint cancels notification only; process teardown remains
  `PlatformDraining`, the endpoint-bearing overload is rejected in `Exiting`,
  and the endpoint-free completion/visibility path drains all external authority
  before region reclaim;
- an old endpoint cannot deliver into a recycled process generation, and events
  expose neither provider tokens nor raw hardware/topology identity.

## Remaining Phase-6 tests

- stale lifecycle completion cannot transition a recycled process;
- park waits for platform completion when required;
- cancellation of a platform operation cannot return an owned buffer before Phase 2/4 closure;
- cancellable asynchronous endpoint waiting composes with process/channel teardown
  without turning a wait cancellation into platform-operation completion;
- external toolchain qualification records versioned evidence without changing native API semantics.

## Next implementation pool and external boundaries

The next pool should add one exact cancellable asynchronous wait over
`KernelEventEndpoint`, following Track-A waiter teardown rules: endpoint/process
close cancels only an uncommitted wait, while an already committed event remains
the source result until consumed or the endpoint itself is explicitly closed.
Wait cancellation must not become DMA/platform cancellation or ownership-return
proof. This remains a local runtime contract, not evidence of HybridCPU WFE/SEV,
timer or executable DMA delivery.

Real timer binding remains `ExternalBlocked` under `EXT-HCPU-002`. Real
HybridCPU scheduler-policy admission remains `ExternalBlocked` under
`EXT-HCPU-003`. HybridCPU `master` has no neutral DMA submit/completion/cancel
surface; the separately pinned visibility head likewise does not provide one.
Executable DMA therefore remains `ExternalBlocked` under `EXT-HCPU-004`.
Reproducible AOT/image/ISE qualification remains
`ExternalBlocked` under `EXT-HCPU-001`. None of those boundaries is replaced
with a locally fabricated success path.

## Acceptance criteria

Phase 6 is complete when process lifecycle state is causally tied to neutral
HybridCPU execution lifecycle, one reusable event/completion primitive handles
typed asynchronous platform sources and provides cancellable asynchronous
consumption with exact endpoint/process teardown semantics, and the AOT/image/ISE
path is either reproducibly qualified or explicitly remains `ExternalBlocked`
with no fabricated fallback claim.

## Do not do

- no exact-cycle scheduling ABI;
- no physical lane allocation in Sing kernel;
- no POSIX signal semantics as a kernel foundation;
- no toolchain-specific types in native app/service contracts;
- no claim of hard real-time without timing evidence.
