# Phase 3 — Real neutral HybridCPU domain binding

## Status

**First cross-repository integration phase.** Depends on Phases 1–2 and on a stable exported HybridCPU runtime integration surface. Corresponds primarily to `EXT-HCPU-003`.

## Goal

Materialize a live SingNextOS security principal into neutral HybridCPU execution, memory and I/O authority without importing VMX/VMCS or HybridCPU internal descriptor types into SingNextOS.

Target relation:

```text
Sing PlatformDomainIdentity(DomainId, ProcessGeneration)
  -> privileged HybridCpuPlatformAuthorityProvider
  -> opaque provider domain lease
  -> HybridCPU neutral execution/memory/I/O domain context
```

The local identity remains authoritative for OS policy. HybridCPU remains authoritative for live platform admission.

## Why this is a bridge, not an ID mapping

The adapter must preserve:

```text
SingNextOS DomainId != raw HybridCPU runtime domain id/tag/handle
```

HybridCPU currently has code-confirmed neutral runtime concepts such as execution-domain descriptors, memory/address-space domains, I/O domains, typed grants and `DomainRuntimeContext`. These are implementation/runtime authority objects, not the Sing capability namespace.

The provider may correlate them internally, but ordinary Sing code sees only an opaque platform lease and semantic feature/result types.

## Provider responsibilities

A first real `HybridCpuPlatformAuthorityProvider` should do only a narrow set of things:

1. bind/adopt a neutral execution domain;
2. bind/adopt its memory/address-space authority;
3. bind/adopt an I/O domain when available/required;
4. return one opaque composite lease with independent provider generation/epoch;
5. transition start/park/resume/stop using neutral runtime operations;
6. revoke/close all components in a deterministic order;
7. report `Unsupported`, `Denied`, `Stale`, `Revoked` and `Faulted` distinctly.

Do not add DMA, compute, VM or display methods to this provider slice yet. Those build on the domain lease in later phases.

## SingNextOS-side refactoring

### 1. Make domain requirements explicit but semantic

Add a small `DomainRequirements`/profile describing intent, for example:

- ordinary service;
- device service;
- compute-capable service;
- virtual-domain parent;
- requested memory/I/O isolation class;
- scheduling budget/priority hints if supported.

Do not include lane IDs, SMT thread IDs, VLIW slot masks or VMCS fields.

### 2. Keep one Sing domain abstraction with optional materialization

`DomainId` should remain the common control-plane identity for:

- application/process isolation;
- SIP service isolation;
- privileged device services;
- VM manager/virtual domains.

But these uses do **not** need identical HybridCPU materialization. An ordinary service may use one execution+memory binding; a VM adds child/nested domain, guest address space and event/trap state later.

### 3. Add runtime binding lifecycle hooks

Current `StartProcess`, `ParkProcess` and `ResumeProcess` only update local states. After a real binding exists, lifecycle order becomes:

```text
local admission
  -> platform domain bind
  -> provider transition
  -> local Running/Parked publication
```

If provider transition fails, local state must not claim a stronger execution state than the platform reached.

Use Phase 1 completion receipts when transitions can be asynchronous.

### 4. Keep capability policy in Sing

The HybridCPU provider must never mint local object capabilities. Before any provider transition, Sing validates the subject/process generation and required local rights. HybridCPU typed grants are the second half of the `AND`, not a replacement for `CapabilityAuthority`.

## HybridCPU-v2 change expected

Prefer a **stable exported integration facade around existing neutral runtime owners**, not ISA or scheduler redesign.

If existing public surfaces are insufficient, HybridCPU should add a narrow provider-facing API that can:

- create/adopt/bind neutral execution+memory+I/O domains;
- return opaque handles/epochs;
- perform lifecycle transitions;
- close/revoke them;
- expose typed readiness/failure without leaking internal descriptors.

No VMX instructions, VMCS manager, global capability register file or lane-control ABI is justified by this phase.

## Tests

Use both host and real-provider conformance:

- wrong `ProcessGeneration` denied before external call;
- duplicate active bind denied;
- provider returns mismatched subject → bridge closes it and faults;
- stale provider generation cannot be reused;
- start/park/resume local state follows provider completion, not request intent;
- termination cannot reclaim before provider domain closure;
- no VMX type is referenced by core domain contracts;
- an ordinary SIP service can run without nested-domain machinery.

## Acceptance criteria

Phase 3 is complete when one Sing process/service can be admitted, bound to a live neutral HybridCPU domain, transitioned through lifecycle states and fully torn down with stale-handle rejection, while all external identities stay bridge-private.

## Do not do

- no `DomainId.Value == HybridCPU DomainId` shortcut;
- no VMCS-backed process model;
- no lane placement API;
- no scheduler rewrite unless a concrete exported runtime operation is impossible without it;
- no claim that every Sing service is a nested VM.
