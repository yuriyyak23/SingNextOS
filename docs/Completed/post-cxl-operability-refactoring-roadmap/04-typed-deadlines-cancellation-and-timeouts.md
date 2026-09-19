# Phase 4 — Typed Deadlines, Cancellation, and Timeouts

## Goal

Unify temporal control semantics across IPC, devices, DMA/CXL, compute, virtualization, service drain and process teardown without conflating timeout with effect closure.

## Core contracts

Introduce provider-neutral contracts equivalent to:

```text
Deadline
DeadlineClockClass
CancellationScopeId
CancellationGeneration
CancellationRequest
CancellationDisposition
TimeoutDisposition
CancellationObservation
```

Use monotonic runtime time for correctness. Wall-clock timestamps may be included for diagnostics only.

## Cancellation dispositions

Minimum semantic outcomes:

```text
CancelledBeforeEffect
CancellationRequested
TooLateEffectMayExist
CompletedBeforeCancellation
ProviderClosurePending
ProviderEffectContained
Unsupported
Stale
```

Avoid a single boolean `Cancelled` result for effect-capable work.

## Parent/child scopes

Support bounded hierarchy:

```text
Service operation deadline
 -> IPC request deadline
   -> provider/external-operation deadline
```

A child may be equal to or stricter than a strict parent deadline; it may not extend the parent correctness boundary.

Cancellation propagation is explicit and typed. A parent cancellation request does not imply every child effect has closed.

## ExternalOperation integration

Map cancellation to lifecycle stages:

- Prepared/Admitted: may close locally if no provider effect exists;
- Submitted: request cancellation, await provider result/closure;
- DeviceComplete/Visible: effect already happened; cancellation may suppress later publication only if policy permits;
- Published: cancellation cannot undo architectural publication;
- Released: terminal observation only.

Never release RegionUse/device/provider pins solely because deadline expired.

## IPC integration

For request/reply:

- timeout can stop caller waiting;
- callee request may still be executing unless cancellation disposition proves otherwise;
- MOVE ownership state follows transfer admission, not caller wait timeout;
- borrowed payload lifetime cannot end while receiver still has a valid admitted borrow.

## Supervisor integration

Drain policies may specify deadlines. On deadline expiry supervisor chooses among:

- continue draining;
- quarantine;
- fail service replacement;
- use provider-specific containment already proven by existing authority.

It cannot locally reclaim uncertain effects.

## Idempotence

Cancellation requests are idempotent per cancellation generation. Reusing an old scope for a new operation is stale.

## Required tests

- deadline before provider effect -> clean local cancellation;
- deadline races provider submission -> deterministic disposition;
- timeout after accepted effect -> no local reclaim until closure;
- cancellation after DeviceComplete before publication -> publication policy exercised correctly;
- cancellation after Published -> reported too late;
- stale cancellation generation rejected;
- parent cancellation propagates request without falsifying child closure;
- IPC timeout does not duplicate MOVE ownership;
- service drain timeout with ambiguous external effect -> quarantine.

## Exit criteria

At least IPC and one external/provider path use the common contracts, with tests proving that timeout/cancellation never fabricate effect closure or ownership return.

## Implemented status (2026-09-18)

- Added versioned provider-neutral monotonic deadline and generation-bound cancellation contracts, including all required typed dispositions. Observation DTOs explicitly authorize neither effects nor reclaim.
- Added parent/child scopes with non-extending effective deadlines, explicit propagation, idempotent request, exact process-generation ownership and typed stale observations.
- Leaf scopes are single-consumer: reuse for a different IPC/external operation is rejected before that operation's admission.
- Integrated scopes into `ExternalOperation` admission and cancellation. Pre-effect expiry cancels locally; Submitted and pre-publication outcomes retain pins until the existing authority proves provider closure/containment; Published remains published and reports too late.
- Added typed session invocation wait cancellation. The caller may stop waiting with `CancellationPending`, while the invocation remains tracked for exact service settlement. An admitted MOVE remains owned by the receiver and is never returned or duplicated merely because caller waiting ended.
- Added supervisor drain timeout policies for continue, quarantine, fail replacement and require proven containment. No timeout policy fabricates reclaim.
- Executable evidence and qualification results are recorded in `04_PHASE_04_IMPLEMENTATION_EVIDENCE.md`.

FutureGated: device-family-specific cancellation adapters beyond the shared `ExternalOperation` boundary remain provider work; budget charging of scopes/waiters belongs to Phase 05; trace events and timeout metrics belong to Phases 06 and 09. No automatic wall-clock scheduler is introduced—the correctness clock is the injected monotonic runtime `TimeProvider`.
