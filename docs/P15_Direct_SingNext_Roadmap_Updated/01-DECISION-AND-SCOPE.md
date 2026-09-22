# Decision and Scope

## Architectural decision

Implement Direct SingNext Boot as a SingNextOS-owned boot path with four distinct responsibilities:

1. `HybridCpu.Boot.Contracts` — versioned wire/ABI contracts only.
2. `SingNext.Boot.Core` — platform-neutral boot policy, deterministic state machines, parsing, verification, rollback/recovery semantics.
3. `SingPlus.Platform.HybridCpu.Boot` — pre-kernel HybridCPU/platform hardware adapter.
4. `SingNext.Boot.Capsule` — minimal executable composition root.

The composition root depends on Core and the HybridCPU boot adapter. The boot adapter never depends on the capsule.

## In scope

- boot selection and BootVolume parsing;
- manifest/signature/hash validation policy;
- capsule A/B and OS-image A/B with separate generation domains;
- rollback floor and trial-state handling;
- bounded local recovery;
- bounded PCIe/CXL Type-3 discovery for the first supported profile;
- temporary single-target HDM mapping transaction;
- verified copy to normal RAM;
- versioned `HybridBootInfo` handoff;
- kernel-side BootInfo ownership and validation;
- fresh runtime liveness/generation revalidation;
- creation of new runtime provider generations through the existing owner;
- retirement/quarantine of boot-only mappings;
- static admission and NativeAOT evidence;
- ISE and hardware qualification gates.

## Out of scope

Do not expand P15 into:

- CXL interleave;
- MLD;
- multi-host pooling;
- network recovery;
- CXL IDE;
- accelerator boot;
- confidential compute;
- general runtime CXL redesign;
- changes to HybridCPU-v2 source.

## External dependency rule

HybridCPU-v2 is **read-only** for this roadmap.

P15 may:

- inspect current source/docs;
- check existing external contracts;
- pin and qualify an external baseline;
- define required external behaviors;
- fail closed when a required behavior is absent;
- block a PR/claim behind an external feature gate.

P15 may not:

- include a HybridCPU-v2 code PR;
- assume a missing compiler/loader/reset/ISE behavior exists;
- silently compensate for a missing external contract in SingNext authority semantics.

## Single-owner rule

P15 must not create a second authoritative owner for:

- capabilities;
- Region ownership;
- provider generations;
- boot selection durable state;
- rollback floor;
- external-operation lifecycle.

The existing runtime owners remain authoritative after fresh admission.

## First-supported profile

The first implementation profile is intentionally narrow:

- one local signed capsule source;
- one selected BootVolume;
- one temporary single-target HDM aperture;
- no interleave/MLD/multi-host;
- fail closed on ambiguous topology or mapping state.
