# Phase 0 — Baseline and adoption rules

## Goal

Turn the historical Singularity review into a bounded architectural input. Prevent “porting Singularity” from becoming a justification for importing obsolete implementation assumptions.

## Adopt

Carry forward these principles:

- explicit interface/implementation/library/driver separation;
- contract-first communication;
- ownership-aware cross-component data movement;
- bounded device-resource descriptions;
- explicit interrupt registration/wait/ack/release lifecycle;
- declarative component/service metadata;
- isolated drivers and system services;
- rich source-facing libraries over a narrow privileged substrate;
- close/failure/teardown as protocol semantics rather than cleanup afterthoughts.

## Adapt

Translate historical concepts into current SingNextOS primitives:

```text
historical concept       current target
----------------------  -------------------------------------------
channel contract         generated typed SIP contract
shared/exchange heap     OwnedRegion/OwnedBuffer + grants
I/O configuration       DeviceResourceSet + exact leases
IRQ object              kernel event/completion + IrqBindingLease
manifest/binder          ServiceManifest + admission/binding
process/service          ComponentInstance over domain/process
```

## Reject as implementation targets

Do not migrate:

- Bartok/Sing#/Spec# toolchain dependencies;
- the old x86 scheduler implementation;
- PIC/APIC-specific driver ABI;
- raw physical addresses as public driver authority;
- a global ExchangeHeap/shared mutable transport;
- old GC/runtime assumptions in privileged code;
- a universal HAL that duplicates HybridCPU's neutral execution/memory/I/O domains;
- POSIX, Win32 or VMX as native Sing authority models.

## Current baseline rule

Before each implementation PR derived from this plan:

1. read the current `master` head;
2. read the relevant HybridCPU roadmap phase;
3. inspect current code/tests before relying on documentation status;
4. treat merged implementation and tests as stronger evidence than stale roadmap text.

At the creation of this plan, `SingNextOS/master` is `cc00bdd3a9f7d143044a3a981c755e07b485f873` and already contains the Phase 2 process-exit orchestration merge.

## Acceptance criteria

Phase 0 is satisfied when later PRs can point to a specific item in this plan and state whether the change is an adopted invariant, a modern adaptation, or an explicitly rejected historical implementation pattern.
