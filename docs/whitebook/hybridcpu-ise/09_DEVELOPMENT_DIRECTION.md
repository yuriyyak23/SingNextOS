# 09. Development Direction

## Goal

Этот раздел задаёт dependency order для SingNextOS и сохраняет исходный
pre-Phase-0 baseline как исторический контекст. Он не обещает одновременную
реализацию всех системных services. Его задача — не позволить
filesystem/network/GUI/compatibility work создать параллельные низкоуровневые
security/memory mechanisms в обход capability, ownership и platform authority
boundaries.

> Current-status overlay (2026-09-04): the qualification iteration starts from
> SingNextOS `108195c...` and audits HybridCPU-v2 `9e001bf...`, superseding the
> delivery-status claims in the historical baseline below. Phases 1–5 and the
> locally owned Phase-6 lifecycle, scheduler-policy and event/wait slices are
> complete. This qualification changeset reproducibly digest-binds the managed
> kernel build and admission artifacts, but the external path stops at
> `ManagedAssemblyToHybridCpuAot`. `EXT-HCPU-001` remains `ExternalBlocked`; the
> image stage is `NotProduced` and ISE execution is `NotAttempted`. Phase-7
> Slices 1–3 now provide bounded DSC1 `UInt8` Copy as a Host `ModelOnly`
> reference lifecycle over separate compute capability, owned regions and
> exact completion/cancellation closure, plus an observation-driven wakeup
> through the existing generation-bound `KernelEventEndpoint`. Output/custody
> settles before event commit, and notification cancellation never replaces
> provider drain. Private RuntimeKernel admission serialization plus bridge
> lifecycle-ledger checks now also prevent
> active or ambiguously accepted DMA and DSC1 from sharing the same full mapping
> identity until their exact release boundary. It is not range-level coherence
> or an executable provider claim.
> HybridCPU executable compute remains unavailable under `EXT-HCPU-005`; no
> ISE/compiler code is consumed or changed.

## Historical baseline delta

Roadmap baseline перед Phase 0 implementation был
`faafa6848d832e2ddd3ac1cdb82ec518f8dd5cb3`. Он включает Track A merge
`7977043e98db50673d4d7053c6ddcb9f1beea91b` и local Platform Authority Bridge
work из PR #12/#13.

На этом baseline уже реализованы:

- typed SIP response transport/publication and generated client runtime adapters;
- exact correlated async response waiting;
- deterministic cancellation of pending response waiters on channel teardown;
- process-level channel teardown even when another process keeps the same domain alive;
- committed response preservation when peer teardown happens after publication;
- local `SingPlus.Platform` abstraction contract;
- host `IPlatformAuthorityProvider`;
- neutral local domain binding;
- direct owned-region mapping abstraction;
- local/provider generation separation;
- capability-before-provider validation;
- region mapping reservation that blocks incompatible ownership lifecycle;
- capability revocation cascade into platform mapping drain/revoke.

Это закрывает **Track A как Current local foundation** и сохраняет Platform Authority Bridge как **CurrentModelBound**. Оно не закрывает `EXT-HCPU-003`/`004`: реального HybridCPU provider, IOMMU/DMA/rebind/coherence semantics в репозитории нет.

## Architecture target

```text
.NET-like Application / System API
  -> generated typed SIP client
  -> service SIP
  -> capability + ownership IPC runtime
  -> privileged kernel authority
  -> Platform Authority Bridge
  -> HybridCPU neutral runtime domains/providers
  -> execution/memory/compute/device planes
```

The system preserves independent proof layers:

```text
static producer proof
runtime local authority
external platform authority
publication/commit authority
```

No layer substitutes for another.

## Track A — complete SIP response/publication semantics

### Status

**Current / completed local foundation.** PR #14 closes typed response publication and PR #15 closes generated client/runtime adapter plus teardown semantics. Later platform completion work must compose with this response model rather than introduce a second publication authority.

### Current outcomes

- typed response payload transport;
- response shape/cardinality validation before publication;
- ownership-return response transfer with generation/lifetime rules;
- explicit response publication/cancellation semantics;
- malformed service response fails before client-visible publication;
- deterministic protocol digests/manifests remain stable;
- exact correlated async response waiting without polling;
- deterministic channel/process teardown cancellation;
- already-published responses remain committed across later peer teardown.

## Track B — Platform Authority Bridge local v1

### Status

**CurrentModelBound.** The local/host-backed bridge is current, but its current feature-bit and synchronous revoke surface needs Platform Contract vNext lifecycle/completion before real HybridCPU integration.

Current v1 exposes:

```text
NeutralDomainBinding
DirectOwnedRegionMapping
```

and a deterministic host provider.

### Next local evolution

Add new bridge families only when concrete external/use-case requirements exist. Avoid pre-creating broad abstractions for every ISE feature.

Immediate next work is the phased roadmap sequence:

```text
Platform Contract vNext
 -> explicit revocation/completion lifecycle
 -> real neutral HybridCPU domain binding
 -> exact non-coherent-safe region mapping
 -> bounded DMA vertical slice
```

Constraints remain:

- no HybridCPU internal type references;
- no raw lanes/opcodes;
- no VMCS authority store;
- no provider token leakage to SIPs;
- every stateful binding generation/lifetime-bound.

## Track C — real HybridCPU domain binding

### Status

**Current for the exact neutral lifecycle; externally incomplete for later
feature families.** `HybridCpuPlatformAuthorityProvider` now binds an exact
SingNextOS process subject to the neutral HybridCPU runtime and provides
synchronous definitive `Start / Park / Resume / Close` transitions. HybridCPU
scheduler-policy admission remains `ExternalBlocked`, and the provider does not
turn this lifecycle binding into executable DMA, compute, virtualization or
security authority.

### Required outcomes

- bind a SingNextOS principal to real neutral platform authority;
- distinguish unsupported/denied/stale/revoked states;
- preserve `DomainId != raw platform handle`;
- termination closes external work before local reclaim;
- no VMX compatibility object becomes authoritative local state.

Tracked by `EXT-HCPU-003`.

## Track D — hardware-backed owned-region mapping

### Status

**CurrentModelBound locally; ExternalBlocked for real direct mapping.**

Current local mapping requires exact `MemoryRegion` capability and current
region ownership, then reserves the region against transfer/loan/release. The
local runtime also conservatively excludes active DMA and DSC1 use of the same
complete mapping identity while permitting independently authorized distinct
mappings. It retains ambiguous/faulted uses rather than reclaiming through
uncertain external state, or quarantines their containing platform domain when
an exact operation identity cannot be retained.

### Required external closure

- exact range/access mapping;
- revoke/unmap;
- stale binding rejection;
- drain/cancel before ownership rebind/reclaim;
- truthful coherence/direct-access capability statement.

Do not infer page remap, IOMMU or cache behavior from the local interface.
In particular, this policy neither proves range/cache-line compatibility nor
versions CPU alias mutations after DMA preparation. Executable reuse still
needs a provider-side drain and mutation/visibility epoch or fresh-prepare rule.

Tracked by `EXT-HCPU-004`.

## Track E — first narrow ISE compute provider

**In progress locally; ExternalBlocked for ISE execution.** Phase-7 Slices 1–3
implement the bounded DSC1 Copy contract, a `RuntimeKernel` CPU-staged
reference copy admitted by a Host `ModelOnly` lifecycle provider, and
generation-bound observation wakeup through the existing event endpoint,
alongside fail-closed feature selection and a conservative whole-mapping
DMA↔DSC1 interlock. A real provider still depends on an actual stable neutral
external interface, not internal ISE breadth.

Candidates remain:

### DSC1 BulkCompute

Strong fit for owned regions and all-or-none semantics:

```text
Copy / Add / Mul / Fma / Reduce
```

Do not claim DSC2 queues or coherent async overlap.

Current local v1 supports only `UInt8/AllOrNone Copy`, disjoint owned regions
and at most 1 MiB. It does not yet expose Add/Mul/Fma/Reduce. Provider denial,
stale/forged identity, malformed completion and cancellation cannot publish
output. Exact terminal observation may publish one local `Completion` wakeup
only after output/reservation settlement; operation closure still precedes
mapping/domain/local reclaim.

An accepted or ambiguously accepted local DSC1 operation excludes active DMA
on each complete source/destination mapping, and an active/ambiguous DMA
submission excludes DSC1 on its complete mapping. Same-mapping read/read and
non-overlapping byte ranges remain conflicts; accepted lifetimes on distinct
mappings may overlap although admission is coarse-serialized.
DMA completion retains its use until post-completion visibility, while DSC1
retains both uses through completed/cancelled local settlement. Faulted or
ambiguous state retains its exact use where identifiable, or quarantines the
containing platform domain otherwise. This adds no provider ABI and proves no
cross-engine coherence or hardware execution.

The next local Phase-7 slice is the generated typed `ComputeService` SIP ingress
for the exact source-read and destination-exclusive authorities, with atomic
validation/rollback and ownership return. It should not broaden the compute
operation set or invent a universal accelerator contract.

### MatrixTile v1

Strong fit for AI/HPC but needs typed shape/numeric/layout contracts and owned-region ingress/egress.

### Scoped L7-SDC accelerator

Strong device story, but memory ordering/coherence requires explicit provider contract.

Tracked by `EXT-HCPU-005`.

## Track F — scheduler/event integration

**Current for the locally owned Phase-6 contour; partially HybridCPU-bound.**
Exact process-scoped execution lifecycle, binding-scoped semantic
budget/priority/latency/throughput intent, IRQ and model DMA-completion delivery
plus model DSC1 terminal delivery through one generation-bound endpoint, and
cancellable `ValueTask` consumption are implemented. The scheduler policy and
DSC1 lifecycle are host `ModelOnly`; the HybridCPU provider correctly reports
those unavailable where no neutral interface exists. HybridCPU supplies the
exact neutral IRQ binding, but no generic timer/event wait, DMA-completion or
compute-completion surface.

Public API remains `Task`/`ValueTask`/cancellation/event/channel-oriented and
does not expose `WFE`, `SEV`, VT IDs or lane placement as native ABI. Real timer
delivery remains under `EXT-HCPU-002`; scheduler-policy admission remains under
`EXT-HCPU-003`; boot/AOT/ISE remains under `EXT-HCPU-001`.

## Track G — evidence and replay diagnostics

**BridgeRequired.** Expose external evidence only after a classified provider contract exists.

Potential service outputs:

- legality/reject summaries;
- replay diagnostics;
- compute/device completion diagnostics;
- permitted measurements.

Security rules:

- explicit evidence-read authority;
- visibility classes;
- no host topology leakage by default;
- evidence objects never accepted as capabilities/grants.

## Track H — neutral virtualization

**BridgeRequired; VMX remains ProjectionOnly.** Only after execution/memory/I/O domain bindings exist:

```text
child execution domain
 -> memory domain
 -> I/O/device assignment
 -> trap/event service
 -> checkpoint/evidence classification
 -> optional VMX compatibility projection
```

Do not start from VMXON/VMCS APIs.

## Track I — SecureCompute gate

### Status

**FutureGated.** Open production implementation only when an external provider proves:

- secure-domain lifecycle owner;
- operation-bound admission/grants;
- private/shared memory enforcement;
- evidence publication class;
- backend execution owner;
- completion/publication semantics;
- negative conformance;
- explicit production activation status.

Until then confidential-domain API may exist only as a target shape that reports unavailable/fails closed.

## Track J — native system services

**BridgeRequired downstream.** Filesystem, networking, process management, GUI/compositor, richer device services and compatibility stacks build on the same substrate.

The normative native API/UI model is defined in [`12_NATIVE_API_AND_UI_CONTRACTS.md`](12_NATIVE_API_AND_UI_CONTRACTS.md).

### Native API rule

```text
.NET-like ergonomics
 -> generated typed SIP contract
 -> explicit capability + ownership
 -> kernel authority only where required
```

High-level functions are not added as a huge kernel syscall surface.

### Filesystem/storage

Prefer capability-backed file/session objects and owned regions for large I/O where supported. Copy/sanitization remains valid when required.

### Network

Prefer:

```text
NIC/device capability
+ owned packet region/ring
+ I/O-domain mapping when available
```

not ambient device access or implicit global shared buffers.

### UI/GUI

UI is a standard target Sing+ subsystem, not a KDE/GNOME ABI.

Separate target service roles:

```text
Display
Compositor
Window Manager
Input
Clipboard
Font/Text
Accessibility
Notification
Shell
```

Implementation order may be incremental, but no GUI implementation should bypass capability/ownership rules with a privileged global shared framebuffer or ambient global-input authority.

### Surface presentation

Target semantics:

```text
APP exclusive-write
 -> Present / transfer or controlled grant
 -> compositor read
 -> device/display read where available
 -> completion/fence
 -> APP reacquires write authority
```

This is a software contract target. Concrete GPU DMA/remap/coherence remains external/future until provider evidence exists.

## Track K — compatibility personalities

**ProjectionOnly / optional downstream compatibility.** Win32/POSIX/Wine support is optional downstream compatibility work.

```text
legacy API
 -> compatibility/personality SIP
 -> native Sing+ service contracts
```

Compatibility must not redefine native process, filesystem, network, GUI, capability or ownership semantics.

## Driver strategy

The old universal “driver factory from UHDL” does not block practical drivers.

Recommended progression:

1. capability-declared hand-written SIP driver over platform abstraction;
2. generated SIP stub/state metadata;
3. declarative register/queue descriptors for repeated device families;
4. generated validation/access code;
5. richer hardware-manifest DSL only when real device families justify it.

## Real-time research track

HybridCPU typed-slot/replay/evidence properties make real-time research attractive, but exact-cycle hard real-time is not a current OS guarantee.

Future RT profile requires explicit contracts for:

- bounded execution budget;
- memory/cache latency envelope;
- interrupt/timer latency;
- SMT interference;
- DMA/device completion bounds;
- WCET/schedulability proof;
- fail-closed overload behavior.

## Assist research track

Current assists are bounded warming mechanisms. First OS use should remain prefetch/warming policy. GC/security memory mutation or scanning is a different authority class and cannot be inferred from assist existence.

## Definition of Done for a hardware-backed service

A service is not “HybridCPU integrated” because a host provider or opcode exists.

Required closure:

```text
local contract shape proven
+ local capability/ownership negative tests
+ external feature discovery positive
+ external denial/stale tests
+ exact domain/range binding
+ execution result semantics
+ explicit publication/commit proof
+ cleanup/revocation proof
+ deterministic integration artifact
```

If any piece is missing, classify honestly as `CurrentModelBound`, `BridgeRequired`, `ExternalBlocked`, `ProjectionOnly` or `FutureGated` as appropriate.

## Priority decision

Near-term implementation now starts after completed Track A and follows the roadmap dependency order:

```text
1. Platform Contract vNext feature/completion model
2. explicit revoke/drain/closed lifecycle and reclaim proof
3. real neutral HybridCPU domain binding
4. exact non-coherent-safe owned-region mapping
5. one bounded DMA vertical slice
6. scheduler/events and one narrow compute provider
7. richer filesystem/network/UI services on that substrate
8. compatibility personalities only downstream
```

The UI/API architecture is specified now so later services converge on one ABI, but specification does not imply current implementation.
