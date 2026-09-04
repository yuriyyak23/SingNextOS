# Phase 6 — Execution lifecycle, scheduler interaction, events and boot qualification

## Status

**Complete for the locally owned Phase-6 scope; external AOT/image/ISE remains
`ExternalBlocked`.** Phase 3 already provides synchronous, definitive neutral
`Start / Park / Resume` transitions and publishes local process state only after
exact provider success. Phase 5 already provides the first generation-bound
`KernelEventEndpoint` use through IRQ delivery.

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

The fourth slice adds one exact cancellable asynchronous wait over the same
endpoint. Commit, caller cancellation, explicit close and process teardown are
serialized against one waiter; teardown stops new waits before platform drain
without discarding a staged producer reservation. This is local notification
consumption, not platform-operation cancellation or HybridCPU event hardware.

The fifth slice runs the reproducible build/admission qualification lane and
records why the external path stops at `ManagedAssemblyToHybridCpuAot`. The
managed kernel assembly and admission proof receive per-run digests, while the
missing external toolchain identity/command, HybridCPU image and ISE result stay
explicitly absent. This is the accepted fail-closed Phase-6 outcome under
`EXT-HCPU-001`, not a claim that external qualification succeeded.

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

## Completed slice D — exact cancellable event wait

The public wait surface adds no new authority or completion token:

```text
WaitForKernelEventAsync(
  exact ProcessHandle,
  exact KernelEventEndpoint,
  CancellationToken)
    -> ValueTask<KernelResult<KernelEvent>>
```

Owner and endpoint generation are validated before waiter registration. Each
endpoint admits at most one asynchronous waiter, independently of its one event
mailbox slot, so a producer can stage an event while its consumer is already
waiting. A previously committed event is consumed immediately and exactly once,
including when caller cancellation arrives later.

The registry serializes all terminal races under one gate:

- commit first transfers the exact event to the registered waiter; later caller
  cancellation, endpoint close or process teardown cannot replace that result;
- caller cancellation first removes only that exact waiter; a later commit is
  retained as the endpoint's pending event for a subsequent wait or consume;
- explicit endpoint close first cancels an uncommitted waiter and discards unread
  notification state, but a staged producer keeps close in `PlatformBindingDraining`;
- process teardown changes the endpoint from `Active` to `OwnerClosing`, cancels
  current waiters and rejects new wait, consume and stage admission before any
  external drain, while already staged work may still commit or roll back;
- final endpoint reclaim is all-or-none for the process and remains draining
  while any producer owns a staged publication reservation.

Wait cancellation completes the `ValueTask` as cancelled rather than fabricating
a successful event. Identity/admission failures remain typed `KernelResult`
errors. No path converts cancellation into IRQ/DMA completion, visibility,
grant closure, ownership transfer or permission to reclaim memory.

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

Cancelling only `WaitForKernelEventAsync` is narrower still: it neither closes
the endpoint nor changes its producer or platform authority. Process teardown
cancels registered event waiters alongside Track-A response waiters before
external drain, but retains staged and committed local notification state until
the final reclaim boundary. An exact event already committed to a waiter remains
that waiter's result.

## Completed slice E — reproducible boot/AOT/ISE qualification boundary

SingNextOS does not redesign itself around the external toolchain. The
qualification treats that toolchain as a black-box lane:

```text
build the managed Sing kernel candidate
  -> local admission proof
  -> external HybridCPU AOT/image toolchain
  -> HybridCPU image
  -> ISE execution
```

The deterministic negative qualification lane records across its log and
machine-readable report:

- exact clean SingNextOS workflow commit;
- exact HybridCPU source revision, SDK and compiler/runtime contract version;
- resolved runner and .NET SDK identities;
- deterministic `ReproductionCommands` recipe plus workflow-log execution receipt;
- managed kernel assembly digest;
- admission proof digest;
- the first unavailable external stage and explicit `null` toolchain, image and
  ISE fields.

This lane is deliberately scoped to the audited negative result. It neither
accepts a caller-supplied positive toolchain result nor classifies later loader
or runtime failures. A future positive qualification must extend or replace the
lane only after the external interface required by `EXT-HCPU-001` is supplied.

The qualification changes are based on SingNextOS `108195c...`; each run records
its exact clean SingNextOS `HEAD`. For the audited `9e001bf...` HybridCPU-v2
baseline, the local build and admission steps produce digest-bearing evidence.
The HybridCPU compiler accepts already constructed `VLIW_Instruction` carriers,
not a managed assembly, and no published prebuilt assembly-consuming toolchain is
available. The report therefore records:

```text
ManagedAssemblyToHybridCpuAot = ExternalBlocked
ToolchainIdentity/AotCommand  = null / null
ImageGeneration/ImageDigest  = NotProduced / null
IseLoaderAcceptance/IseResult = NotAttempted / null
```

`SingPlus.Kernel.dll`, rooted at
`SingPlus.Kernel.KernelEntryPoint::Run`, is the admitted managed candidate.
`SingPlus.Boot.dll` remains a `User` / `ManagedGc` host smoke harness that
depends on `SingPlus.Kernel.Hal.Host`; it is not relabelled as a HybridCPU image.
The exact stage evidence and reproduction commands are recorded in
`EXT-HCPU-001` and in the per-run
`artifacts/hybridcpu-aot-qualification/SingPlusHybridCpuQualificationV1.json` CI
artifact. `SHA256SUMS` in the same directory binds the report and both copies of
the compared artifacts. Host-side architecture tests remain independent.

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

## Completed slice-D tests

- a waiter registered before IRQ or DMA publication receives the exact committed
  event once, while synchronous consume cannot duplicate that delivery;
- a provider completion failure rolls back only the staged reservation, publishes
  nothing and leaves the waiter available for the later successful retry;
- an event committed before token cancellation remains the waiter result, and an
  already pending event wins over a pre-cancelled token;
- caller cancellation first removes only the exact waiter; the endpoint remains
  reusable and a later IRQ or DMA completion is retained and delivered;
- one endpoint rejects a second waiter, and explicit endpoint close cancels the
  uncommitted first waiter;
- stale endpoint generation, foreign owner, closed endpoint, stale process handle
  and a recycled `ProcessId` are rejected without replacing a valid waiter;
- process teardown cancels both Track-A response and event waiters while an
  intentionally deferred external mapping remains draining and unreclaimed;
- teardown that has already closed its platform domain still pins process reclaim
  while a racing IRQ producer owns a staged event, then completes only after the
  exact publication commits;
- wait cancellation does not call a DMA provider, close a grant or permit buffer
  transfer before exact completion and post-completion visibility;
- a staged DMA completion remains invisible to its waiter and keeps endpoint close
  draining until the exact provider observation commits;
- the public wait signature contains only local process/endpoint identity and
  ordinary .NET cancellation, with no provider or hardware-topology token.

## Completed slice-E evidence

- the qualification branch starts at fresh-master SingNextOS `108195c...` and
  records the exact clean per-run SingNextOS `HEAD`; it pins HybridCPU-v2
  `9e001bf...`, SingNextOS SDK `10.0.204`, HybridCPU-v2 SDK `10.0.201` and
  HybridCPU compiler/runtime contract version `6`;
- two clean Release builds on pinned `ubuntu-24.04` must reproduce the kernel,
  host-boot and admission-proof bytes; the actual CI log captures the resolved
  runner identity and command execution, while the `ReproductionCommands`
  recipe, checked-out commits, `ComparedArtifactSets = 2`,
  `Comparison = ByteIdentical`, artifact SHA-256 values and embedded admission
  digests are recorded in
  `SingPlusHybridCpuQualificationV1`;
- the recorder validates the exact `SingPlus.Kernel` and `SingPlus.Boot` managed
  PE names, reruns the current `AdmissionVerifier`, requires the regenerated
  canonical proof to equal the supplied proof, and records
  `Stages[LocalArtifacts].Outcome = Validated`,
  `Stages[LocalAdmissionProof].Outcome = Validated` and
  `Stages[LocalArtifactComparison].Outcome = Validated` rather than claiming
  that its JSON proves either build invocation ran;
- the admitted candidate and root are `SingPlus.Kernel.dll` and
  `SingPlus.Kernel.KernelEntryPoint::Run`; the host-HAL-dependent
  `SingPlus.Boot.dll` is explicitly excluded from HybridCPU image claims;
- the external source/release audit finds no supplied command that consumes the
  managed assembly: the current HybridCPU compiler consumes VLIW carriers and
  its `ProgramImage` is not accepted as evidence for the missing AOT stage;
- blocked output remains structurally negative: toolchain identity/command and
  image digest are `null`, image is `NotProduced`, and ISE is `NotAttempted`;
- the qualification adds no toolchain type, raw opcode, lane identity or
  provider token to a native/SIP contract.

The JSON report, its successful parsing and all recorded digests remain
evidence, not authority. Only the workflow log proves command execution, and no
local evidence authorizes external AOT, image loading or ISE execution.

## Deferred external-contract gates

- If a future execution provider changes the current synchronous definitive
  lifecycle into an asynchronous contract, stale completion must not transition
  a recycled process and park must wait for exact provider completion.
- If a future external operation-cancellation receipt is introduced, cancellation
  must not return an owned buffer before Phase-2/4 closure.
- When an assembly-consuming HybridCPU AOT/loader toolchain is supplied, rerun
  `EXT-HCPU-001` and require an image digest plus exact ISE acceptance/execution
  evidence before changing its status.

These are not missing local Phase-6 success paths. The corresponding external
interfaces do not exist in the audited baselines, so SingNextOS keeps them
fail-closed rather than inventing completions or toolchain behavior.

## Phase-6 disposition and next implementation pool

Phase 6 is complete under its stated acceptance rule: locally owned execution,
scheduler-policy and reusable event/wait semantics are implemented, and the
external boot path has a reproducible, explicitly `ExternalBlocked`
qualification record without a fabricated success substitute or a
toolchain-specific public API.

The recommended next pool has now been delivered as Phase-7 Slice 1: bounded
DSC1 `UInt8` Copy over owned-region and completion boundaries, with a Host
`ModelOnly` reference path and a fail-closed unavailable HybridCPU feature.
See `07_COMPUTE_ACCELERATORS.md` and `EXT-HCPU-005`; this historical Phase-6
disposition does not claim the still-missing executable HybridCPU contour.

Real timer binding remains `ExternalBlocked` under `EXT-HCPU-002`. Real
HybridCPU scheduler-policy admission remains `ExternalBlocked` under
`EXT-HCPU-003`. Audited HybridCPU `master` `9e001bf...` includes merged,
grant-scoped DMA prepare/acquire visibility evidence, but no neutral DMA
submit/completion/cancel surface.
Executable DMA therefore remains `ExternalBlocked` under `EXT-HCPU-004`.
AOT/image/ISE integration remains `ExternalBlocked` under `EXT-HCPU-001`, with
the first unavailable stage now recorded as `ManagedAssemblyToHybridCpuAot`.
None of those boundaries is replaced with a locally fabricated success path.

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
