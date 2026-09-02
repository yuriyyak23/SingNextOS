# Phase 8 — Neutral virtualization and nested domains

## Status

**After execution/memory/I/O/completion substrate is proven.** Depends on Phases 1–6. Corresponds to the virtualization/nested portion of `EXT-HCPU-006`.

## Goal

Implement virtualization as an ordinary privileged SIP service over the same domain/memory/I/O authority substrate used by services and devices:

```text
VirtualizationService SIP
  -> local VM/domain capability
  -> kernel authority checks
  -> neutral HybridCPU child execution/memory/I/O domain
  -> optional VMX compatibility projection
```

VMX/VMCS must not become the authoritative kernel object model.

## Current HybridCPU direction from the audit

The audited HybridCPU-v2 code deliberately separates generic runtime domain substrate from VMX compatibility paths. VMCS-visible state is a projection over neutral authoritative state, and current backend readiness is incomplete/fail-closed in relevant tests.

Therefore SingNextOS should design against neutral domains and advertise VM hosting only when the provider reports an executable backend.

## SingNextOS service API

Prefer a neutral API family such as:

```text
CreateVirtualDomain(parent, profile, resources)
BindExecution(domain, executionProfile)
CreateGuestAddressSpace(domain, memoryProfile)
MapGuestRegion(domain, guestRange, ownedRegion, access)
BindVirtualDevice(domain, deviceCapability)
ConfigureTrapPolicy(domain, trapProfile)
Start(domain)
Park(domain)
Resume(domain)
InjectEvent(domain, event)
WaitCompletion(domain)
Destroy(domain)
```

A compatibility personality may later implement VMX/VMCS-facing behavior on top of this service when the provider exposes the projection.

Do not offer core APIs like `CreateVmcs`, `WriteVmcsField`, `SetEptPointer` or `VMLAUNCH`-style authority operations.

## Domain abstraction rule

One Sing `DomainId` vocabulary can cover process/service/VM control-plane identity, but materialization differs:

```text
ordinary SIP service:
  Sing domain + execution/memory binding

virtual machine:
  Sing virtual-domain capability
  + child/nested HybridCPU domain
  + guest address space
  + trap/event state
  + virtual/device I/O bindings

secure VM:
  all above
  + production-positive SecureCompute profile
```

Do not force every service process through nested-domain machinery.

## Nested-domain use outside classic VMs

Nested/child platform domains may also be appropriate for cases that genuinely need delegated hardware authority subsets:

- sandboxed accelerator worker;
- delegated I/O compartment;
- nested VM;
- confidential child domain when supported.

The parent capability set remains the upper bound. Child creation must never amplify authority.

## Kernel vs service boundary

Kernel authority owns:

- create/destroy admission for local virtual-domain objects;
- capability checks;
- exact region/device grants;
- provider child-domain lease ledger;
- stale generation validation;
- final revoke before reclaim.

`VirtualizationService` owns policy:

- VM configuration model;
- guest image policy;
- virtual device model;
- checkpoint/snapshot orchestration;
- VM management UX;
- compatibility personality selection.

These policy objects must not become giant kernel ABI structures.

## Feature discovery

Require separate provider features/readiness for:

```text
VirtualizationDomains = Executable?
NestedDomains = RuntimeAdmission/Executable?
VmxCompatibility = ProjectionOnly/Executable frontend?
```

Presence of VMX parser/projection code does not imply a runnable VM backend.

## Missing pieces for full VM hosting

Do not declare “VM host complete” until provider evidence proves:

- executable child-domain lifecycle;
- guest memory translation/mapping;
- trap/exit completion route;
- event injection;
- virtual/assigned I/O integration;
- deterministic destroy/drain;
- stale restore/checkpoint generations if snapshots are supported.

## Tests

- VM capability required before child-domain provider call;
- child grant set cannot exceed parent;
- VMCS compatibility disabled still permits neutral VM API when backend exists;
- VMX projection cannot mutate authoritative state directly;
- stale guest mapping/device grant rejected;
- VM destroy drains child I/O/compute/memory before local reclaim;
- provider with projection-only VMX reports VM execution unavailable;
- ordinary SIP service path does not allocate nested VM state.

## Acceptance criteria

Phase 8 is complete when a neutral virtual domain can be created, mapped, started, evented and destroyed through the same capability/ownership/completion substrate, with VMX remaining optional and non-authoritative.

## Do not do

- no VMCS as kernel authority store;
- no VMX-centric syscall ABI;
- no assumption that nested domains are required for every process;
- no VM-host claim from parser/projection readiness alone;
- no raw EPT/page-table handles in public contracts.
