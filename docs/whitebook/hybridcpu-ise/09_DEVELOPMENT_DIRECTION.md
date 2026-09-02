# 09. Development Direction

## Goal

Этот раздел задаёт dependency order для SingNextOS после текущего SIP protocol hardening. Он не является обещанием реализовать все подсистемы сразу. Его задача — не позволить проекту уйти в filesystem/network/GUI/legacy-VM детали до того, как сформирован корректный HybridCPU platform boundary.

## Architecture target

```text
Application / System API
  -> generated typed SIP client
  -> service SIP
  -> capability + ownership IPC runtime
  -> privileged kernel authority
  -> Platform Authority Bridge
  -> HybridCPU neutral runtime domains
  -> ISE execution/memory/compute/device planes
```

The system should preserve four independent proof layers:

```text
static producer proof
runtime local authority
external platform authority
publication/commit authority
```

No layer substitutes for another.

## Track A — finish the SIP transport contract

### Why first

HybridCPU integration will produce more hardware-backed responses, staged results, ownership returns and evidence objects. The current SingNextOS request path is already fail-closed and C4 prepared response metadata, but response transport/publication must be as strict as request admission before hardware services are added.

### Required outcomes

- complete typed response payload transport;
- enforce response shape/cardinality against generated metadata;
- implement ownership-return response transfer with generation/lifetime rules;
- define response publication and cancellation semantics;
- make malformed service response fail closed before client-visible publication;
- keep deterministic protocol digests/manifests.

### Why this matches HybridCPU

It mirrors the ISE rule that backend result, completion and retire/publication are separate authority boundaries.

## Track B — local platform abstraction contracts

### Goal

Introduce local interfaces/DTOs that express only semantic OS needs:

- execution-domain lifecycle;
- memory-domain binding;
- I/O-domain binding;
- exact owned-region mapping;
- feature discovery;
- typed external result/rejection vocabulary.

### Constraints

- host implementation first for tests;
- no HybridCPU internal type references;
- no raw lane/opcode API;
- no VMCS manager;
- no external handle leakage to SIPs;
- every stateful binding generation-bound.

This track can be implemented entirely inside SingNextOS while external integration remains blocked.

## Track C — bind domain lifecycle to platform leases

### Goal

Evolve local `DomainRegistry` without turning it into a HybridCPU state mirror.

Create a separate kernel authority that associates a SingNextOS `DomainId` lifecycle with opaque platform execution/memory/I/O bindings.

### Invariants

- local domain can exist without platform lease in host tests;
- required platform profile must fail admission if binding is unavailable;
- termination closes new platform work before local region/capability reclaim;
- stale lease/generation cannot be reused;
- platform denial does not mutate local process state as successful.

## Track D — owned-region platform memory binding

### Goal

Make `OwnedRegion<T>`/`OwnedBuffer<T>` the unit of hardware-visible resource mapping.

### First supported shape

Prefer a deliberately narrow v1:

- exact whole-region or exact range mapping;
- one owner domain;
- read/write direction;
- explicit map/unmap;
- no implicit shared mutable mapping;
- no universal coherency promise;
- no SecureCompute private memory claim.

### Important distinction

Implement **zero-copy ownership rebinding** separately from **DSC offloaded copy**. Tests should make the difference explicit.

## Track E — first ISE compute provider: choose one narrow contour

The first real compute integration should be selected by available external ABI, not by marketing breadth.

### Candidate 1: DSC1 BulkCompute

Strong fit with owned regions and all-or-none staged commit.

Minimal operations:

- Copy;
- Add;
- Mul;
- Fma;
- Reduce.

No DSC2/queue/coherent async claim.

### Candidate 2: MatrixTile v1

Strong fit for AI/HPC but requires typed matrix descriptor/numeric/layout API and region ingress/egress.

### Candidate 3: L7-SDC scoped accelerator

Strong device story but memory conflict/global coherence is currently a larger integration caveat.

### Selection rule

Choose the provider with the smallest stable external surface and clearest conformance path. Do not implement all three simultaneously.

## Track F — scheduler/event integration

After domain bindings are stable, consume existing platform scheduler/event facilities where a binding exists:

- yield;
- event wait/signal;
- barriers;
- timer/interrupt delivery;
- execution budget/priority hints.

Public API stays at Task/ValueTask/event/channel level.

Do not expose `WFE`, `SEV`, VT IDs or lane placement directly.

## Track G — platform evidence and replay diagnostics

### Goal

Expose HybridCPU evidence without turning it into authority.

A read-only service can project:

- legality rejects;
- replay invalidation summaries;
- typed-slot utilization;
- compute/device completion diagnostics;
- permitted measurements.

### Security rules

- explicit evidence capability;
- visibility classes;
- no host-private topology by default;
- no evidence object accepted by capability/ownership APIs;
- stable DTO schema with digest/version.

This is especially useful for performance engineering and reproducibility.

## Track H — neutral virtualization service

Only after execution/memory/I/O domain bindings exist should the OS expose VM lifecycle.

### Order

```text
neutral child execution domain
 -> memory domain
 -> I/O/device assignment
 -> trap/event service
 -> checkpoint/evidence classification
 -> optional VMX compatibility projection
```

Do not start with VMXON/VMCS APIs.

## Track I — SecureCompute integration gate

SecureCompute should have a hard feature gate.

Open implementation only when all of the following are externally proven:

- stable secure-domain binding;
- canonical lifecycle owner;
- operation-bound admission/grant;
- private/shared memory enforcement;
- evidence publication class;
- backend execution owner;
- completion/publication semantics;
- negative conformance tests;
- explicit production activation status.

Until then only API shape/rejection tests may exist.

## Track J — richer system services

Filesystems, networking, GUI/compositor, driver factory and compatibility stacks should build on the platform/domain/ownership substrate rather than create parallel low-level mechanisms.

Examples:

### Network service

Prefer:

```text
NIC capability
+ owned packet region/ring
+ I/O-domain binding
+ direct mapping if supported
```

not hidden kernel bounce buffers by default.

### Filesystem/storage service

Use owned regions for large I/O and device DMA where supported, but preserve explicit copy/sanitization paths for security and compatibility.

### Graphics/AI service

Use MatrixTile/L7 providers through `System.Compute`/`System.Device` abstractions and ownership transfer, not a separate trusted GPU memory manager inside kernel.

## Driver strategy

The old “driver factory from UHDL” should not block practical drivers.

Recommended progression:

1. capability-declared hand-written SIP driver over platform abstraction;
2. generated SIP stub/state metadata;
3. declarative register/queue descriptors for repeated device families;
4. generated validation/access code;
5. only later consider richer hardware-manifest DSL or HDL generation.

This lets the architecture get real device coverage without creating a new compiler/toolchain project prematurely.

## Real-time research track

HybridCPU's typed-slot/replay/evidence model makes real-time research attractive, but a hard real-time product profile must be separate and proof-driven.

A future RT profile needs explicit contracts for:

- bounded execution budget;
- memory/cache/SRF latency envelope;
- interrupt/timer latency;
- SMT interference;
- DMA/device completion bounds;
- WCET analysis;
- schedulability admission;
- fail-closed overload behavior.

Do not infer those guarantees from deterministic lane choice alone.

## Assist research track

Current assists are bounded warming only. The first OS use should therefore be prefetch policy, not GC/security execution.

Potential safe progression:

```text
read-only assist telemetry
 -> explicit prefetch hints for known owned regions
 -> service-level warming policy
 -> research on GC/object-layout prefetch hints
```

Any future assist that mutates memory, scans confidential data or publishes state would be a new authority class and must not be called an “assist” merely to bypass normal checks.

## API direction

Preserve the old WhiteBook's .NET-like user ergonomics, but align APIs with real ownership/domain semantics.

Conceptual namespaces:

```text
System.Compute.Matrix
System.Compute.Bulk
System.Device.Acceleration
System.Device.Dma
System.Virtualization
System.Security.Confidential
System.Diagnostics.PlatformEvidence
```

Objects should be typed capabilities or owned/session resources, not integer handles.

## Definition of Done for a hardware-backed service

A service is not “HybridCPU integrated” merely because a host test passes or an opcode exists.

Required closure:

```text
local contract shape proven
+ local capability/ownership negative tests
+ external feature discovery positive
+ external denial/stale tests
+ exact domain/range binding
+ staged execution result
+ explicit publication/commit proof
+ cleanup/revocation proof
+ deterministic integration artifact
```

If one piece is absent, classify the feature honestly as host-only, external-blocked, policy-only, projection-only or future-gated.

## Highest-value next implementation after this audit

Given the current repository state, the next code iteration should remain small and foundation-focused:

> **finish response publication/ownership transport semantics before introducing any HybridCPU platform service.**

After that, the first HybridCPU-facing code should be a local platform abstraction + host test provider, not a direct ISE/VMX implementation.

This ordering preserves all C1–C4 fail-closed protocol work and prepares the OS for real external authority without premature platform coupling.