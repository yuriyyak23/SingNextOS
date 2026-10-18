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
> complete. `EXT-HCPU-001` remains `ExternalBlocked`; the image stage is
> `NotProduced` and ISE execution is `NotAttempted`. Phase-7 Slices 1–5 now
> provide the complete local Host `ModelOnly` bounded DSC1 `UInt8` Copy path:
> exact platform lifecycle and observation wakeup, conservative DMA↔DSC1
> whole-mapping exclusion, generated product `ComputeService` Borrow+Consume
> ingress, and service→platform→correlated ownership-response composition.
> Because the SIP source remains caller-owned while current platform DSC1 is
> single-subject/owner-bound, the service snapshots the bounded source borrow
> into service-owned staging and keeps the original borrow live through exact
> DSC1 terminal settlement and temporary mapping closure. This is not zero-copy,
> direct borrowed-region execution or hardware evidence. HybridCPU executable
> compute remains unavailable under `EXT-HCPU-005`; no ISE/compiler code is
> consumed or changed.

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

Это закрывает **Track A как Current local foundation** и сохраняет Platform Authority Bridge как **CurrentModelBound**. Оно не закрывает внешние provider requirements: локальная модель не доказывает реальную MMU/IOMMU/DMA/compute/coherence authority.

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

**Current / completed local foundation.** PR #14 closes typed response publication and PR #15 closes generated client/runtime adapter plus teardown semantics. Later platform completion work composes with this response model rather than introducing a second publication authority.

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

**CurrentModelBound.** The local/host-backed bridge is current. New bridge
families are added only for concrete use cases; provider tokens and platform
identities never become public SIP authority.

The current implementation has evolved beyond the historical v1 planning
snapshot with versioned feature discovery, lifecycle/completion, exact mapping,
DMA/visibility, events and bounded DSC1 Host-model families. Those later
families remain feature-scoped rather than forming a universal HAL.

Constraints remain:

- no HybridCPU internal type references in public/native contracts;
- no raw lanes/opcodes;
- no VMCS authority store;
- no provider token leakage to SIPs;
- every stateful binding generation/lifetime-bound.

## Track C — real HybridCPU domain binding

### Status

**Current for the exact neutral lifecycle; externally incomplete for later
feature families.** `HybridCpuPlatformAuthorityProvider` binds an exact
SingNextOS process subject to the neutral HybridCPU runtime and provides
synchronous definitive `Start / Park / Resume / Close` transitions. HybridCPU
scheduler-policy admission remains `ExternalBlocked`, and this lifecycle binding
does not become executable DMA, compute, virtualization or security authority.

### Required outcomes

- bind a SingNextOS principal to real neutral platform authority;
- distinguish unsupported/denied/stale/revoked states;
- preserve `DomainId != raw platform handle`;
- termination closes external work before local reclaim;
- no VMX compatibility object becomes authoritative local state.

Tracked by `EXT-HCPU-003`.

## Track D — hardware-backed owned-region mapping

### Status

**CurrentModelBound locally; externally incomplete for real reusable direct
mapping.**

Current local mapping requires exact `MemoryRegion` capability and current
region ownership, then reserves the region against transfer/loan/release. The
runtime conservatively excludes active DMA and DSC1 use of the same complete
mapping identity while permitting independently authorized distinct mappings.
It retains ambiguous/faulted uses or quarantines their containing platform
domain rather than reclaiming through uncertain external state.

### Required external closure

- exact hardware-backed range/access mapping where needed;
- revoke/unmap;
- stale binding rejection;
- drain/cancel before ownership rebind/reclaim;
- truthful coherence/direct-access capability statement;
- mutation/visibility epoch or mandatory fresh-prepare rule for executable reuse.

Do not infer page remap, IOMMU or cache behavior from the local interface.
Tracked by `EXT-HCPU-004`.

## Track E — first narrow ISE compute provider

**Local Host-model vertical path complete through Slice 5; ExternalBlocked for
ISE/HybridCPU executable compute.**

Phase-7 now proves five local layers for one bounded DSC1 Copy family:

1. `RuntimeKernel` CPU-staged reference copy admitted by Host `ModelOnly` DSC1
   submit/completion/cancel lifecycle;
2. generation-bound observation wakeup through the existing event endpoint;
3. conservative whole-mapping DMA↔DSC1 admission/release interlock;
4. generated typed product `ComputeService` ingress with exact source-read
   Borrow plus destination-exclusive Consume/ownership-return semantics;
5. `RuntimeComputeServiceHost` composition from that typed ingress through the
   existing platform DSC1 lifecycle and back to correlated ownership response.

### DSC1 BulkCompute

The architectural family remains:

```text
Copy / Add / Mul / Fma / Reduce
```

but the delivered local product service intentionally exposes **Copy only**.
There is no justification to add Add/Mul/Fma/Reduce, DSC2 queues or coherent
async overlap while executable Copy itself remains externally blocked.

The current bounded platform lifecycle retains accepted source/destination uses
through exact terminal settlement and local publication/discard. DMA and DSC1
continue to conflict conservatively on the same complete mapping identity.
Faulted/ambiguous state retains exact use where identifiable or quarantines the
containing platform domain. This remains local bridge policy, not provider-side
coherence evidence.

### Typed service composition

The public request remains:

```text
caller exact Compute/Execute capability
+ [Borrows] OwnedBuffer<byte> source
+ [Consumes] OwnedBuffer<byte> destination
-> generated Borrow+Consume OwnershipPair
```

After delivery, source and destination cannot be placed directly into one
current platform DSC1 request without violating the owner-bound mapping model:
source remains owned by the caller while destination has moved to the service.
The service therefore uses a bounded copy adaptation:

```text
live caller source BorrowLease
-> snapshot into service-owned staging
-> exact temporary staging Read mapping
+ exact temporary service destination Write mapping
-> existing SubmitPlatformDsc1Copy / Observe / Cancel
```

The original source borrow remains live until accepted DSC1 use is terminal and
both temporary mappings are closed. On `Completed`, staging/capabilities are
released, the source borrow returns, and destination ownership publishes only
through the existing correlated response transfer. On exact `Cancelled`, the
same platform cleanup happens before borrow return; the response is cancelled
and service-owned destination is released instead of being published as
success. Ordinary bounded/pre-submit denial also cleans up and performs no fake
success. Ambiguous `PlatformFaulted` admission remains pinned and does not
return borrow or destination authority through uncertain external state.

This ordering is the important local proof:

```text
accepted platform use closed
BEFORE source borrow return
BEFORE successful destination ownership response
```

The bounded source snapshot is not zero-copy and does not claim that an
executable provider can consume a caller borrow directly. It is an explicit
adaptation to the current single-subject platform contract.

End-to-end tests drive the generated typed runtime client and cover immediate
Host completion, deferred completion, exact cancellation, bounded-shape failure
with zero provider submit calls, wrong internal service compute authority and
post-cleanup platform-domain revocation.

### External closure

A real provider still depends on a stable neutral executable interface, not
internal ISE breadth. Required missing terms include exact neutral source/
destination custody, accepted-work identity, observe/cancel/drain semantics,
output CPU visibility/acquire proof and close-before-rebind/reclaim.

Tracked by `EXT-HCPU-005`.

### MatrixTile v1

Strong future fit for AI/HPC but requires separately scoped typed shape/numeric/
layout contracts and executable memory custody. It is not folded into DSC1 merely
because both may use lane-6 resources internally.

### Scoped L7-SDC accelerator

Also future/scoped. Memory ordering and command-footprint authority require an
explicit provider contract and are not inferred from the completed local DSC1
Host-model path.

## Track F — scheduler/event integration

**Current for the locally owned Phase-6 contour; partially HybridCPU-bound.**
Exact process-scoped execution lifecycle, binding-scoped semantic
budget/priority/latency/throughput intent, IRQ and model DMA-completion delivery
plus model DSC1 terminal delivery through one generation-bound endpoint, and
cancellable `ValueTask` consumption are implemented. Scheduler policy and DSC1
lifecycle are Host `ModelOnly`; the HybridCPU provider correctly reports those
unavailable where no neutral interface exists.

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

**BridgeRequired downstream.** Filesystem, networking, process management,
GUI/compositor, richer device services and compatibility stacks build on the
same substrate.

The normative native API/UI model is defined in
[`12_NATIVE_API_AND_UI_CONTRACTS.md`](12_NATIVE_API_AND_UI_CONTRACTS.md).

### Native API rule

```text
.NET-like ergonomics
 -> generated typed SIP contract
 -> explicit capability + ownership
 -> kernel authority only where required
```

High-level functions are not added as a huge kernel syscall surface.

### Filesystem/storage

Prefer capability-backed file/session objects and owned regions for large I/O
where supported. Copy/sanitization remains valid when required.

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

Implementation order may be incremental, but no GUI implementation should
bypass capability/ownership rules with a privileged global shared framebuffer
or ambient global-input authority.

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

This is a software contract target. Concrete GPU DMA/remap/coherence remains
external/future until provider evidence exists.

## Track K — compatibility personalities

**ProjectionOnly / optional downstream compatibility.** Win32/POSIX/Wine support is optional downstream compatibility work.

```text
legacy API
 -> compatibility/personality SIP
 -> native Sing+ service contracts
```

Compatibility must not redefine native process, filesystem, network, GUI,
capability or ownership semantics.

## Driver strategy

The old universal “driver factory from UHDL” does not block practical drivers.

Recommended progression:

1. capability-declared hand-written SIP driver over platform abstraction;
2. generated SIP stub/state metadata;
3. declarative register/queue descriptors for repeated device families;
4. generated validation/access code;
5. richer hardware-manifest DSL only when real device families justify it.

## Real-time research track

HybridCPU typed-slot/replay/evidence properties make real-time research
attractive, but exact-cycle hard real-time is not a current OS guarantee.

Future RT profile requires explicit contracts for:

- bounded execution budget;
- memory/cache latency envelope;
- interrupt/timer latency;
- SMT interference;
- DMA/device completion bounds;
- WCET/schedulability proof;
- fail-closed overload behavior.

## Assist research track

Current assists are bounded warming mechanisms. First OS use should remain
prefetch/warming policy. GC/security memory mutation or scanning is a different
authority class and cannot be inferred from assist existence.

## Definition of Done for a hardware-backed service

A service is not “HybridCPU integrated” because a Host provider, generated SIP,
local vertical composition or opcode exists.

Required closure:

```text
local contract shape proven
+ local capability/ownership negative tests
+ external feature discovery positive
+ external denial/stale tests
+ exact domain/range/custody binding
+ executable result semantics
+ explicit visibility/publication proof
+ cleanup/revocation proof
+ deterministic integration artifact
```

If any piece is missing, classify honestly as `CurrentModelBound`,
`BridgeRequired`, `ExternalBlocked`, `ProjectionOnly` or `FutureGated`.

## Priority decision

The authority-first dependency order remains:

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

For Phase 7 specifically, the narrow local Copy vertical path is now complete
through typed service composition. Do not grow arithmetic/queue breadth while
its real neutral executable provider remains `ExternalBlocked`; the next Phase-7
closure belongs to the external facade recorded in `EXT-HCPU-005`.

The UI/API architecture is specified now so later services converge on one ABI,
but specification does not imply current implementation.
