# Phase 6 — Execution lifecycle, scheduler interaction, events and boot qualification

## Status

**After real domain binding.** Runtime scheduler/event work depends on Phase 3. External AOT/image/ISE qualification can proceed in parallel and corresponds to `EXT-HCPU-001`.

## Goal

Connect Sing process/domain lifecycle to HybridCPU execution admission and event mechanisms without making HybridCPU lane topology or exact-cycle scheduling part of the Sing ABI.

## Execution lifecycle

Current `RuntimeKernel.StartProcess`, `ParkProcess` and `ResumeProcess` only update local states. After Phase 3, use a two-layer transition:

```text
Sing validates local process/domain/capability state
  -> provider requests neutral execution transition
  -> HybridCPU runtime admits/completes transition
  -> Sing publishes Running/Parked/etc.
```

If the platform cannot start/park/resume the domain, Sing must not publish the target state merely because the request was issued.

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

## Tests

- provider start failure leaves local process non-running;
- stale lifecycle completion cannot transition a recycled process;
- park waits for platform completion when required;
- scheduler request contains no lane IDs/raw opcodes;
- event routed after process generation change is rejected;
- cancellation of a platform operation cannot return an owned buffer before Phase 2/4 closure;
- external toolchain qualification records versioned evidence without changing native API semantics.

## Acceptance criteria

Phase 6 is complete when process lifecycle state is causally tied to neutral HybridCPU execution lifecycle, one reusable event/completion primitive handles asynchronous platform effects, and the AOT/image/ISE path is either reproducibly qualified or explicitly remains `ExternalBlocked` with no fabricated fallback claim.

## Do not do

- no exact-cycle scheduling ABI;
- no physical lane allocation in Sing kernel;
- no POSIX signal semantics as a kernel foundation;
- no toolchain-specific types in native app/service contracts;
- no claim of hard real-time without timing evidence.
