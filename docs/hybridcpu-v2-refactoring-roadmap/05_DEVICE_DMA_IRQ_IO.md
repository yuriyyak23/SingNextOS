# Phase 5 — Device, MMIO, IRQ and DMA authority

## Status

**Complete — eight vertical slices implemented.**

Implemented slices:

1. exact capability-backed semantic device lease authority;
2. exact capability-backed bounded MMIO lease/range authority;
3. exact capability-backed stale-generation-safe IRQ/event binding;
4. exact admission-only DMA grant authority composed from a live device lease plus an exact mapped owned-region slice, bounded relative range, and direction;
5. exact grant-scoped non-coherent DMA visibility cycles with release/publication prepare evidence and direction-aware post-write CPU acquire evidence;
6. bounded DMA submit admission tied to the exact current prepared-and-unacquired visibility cycle, with a separate Sing-local pending operation identity and fail-closed teardown pinning;
7. exact one-shot DMA completion proof tied to the exact pending local/provider operation, exact grant generation, and exact prepared visibility cycle, with stale/forged/replayed/wrong-generation/wrong-cycle evidence rejected fail closed;
8. direction-aware post-completion visibility plus controlled DMA/mapping/device/domain release through verified mapping closure to `RegionAuthority` CPU transfer/reclaim.

Slice 8 closes the Phase-5 correctness acceptance boundary. A faithful SingNextOS provider path now proves the full bounded non-coherent DMA ordering from exact owned memory through prepare, submit, completion, required post-write acquire, explicit lower-authority closure, and CPU reuse/reclaim, including denial/stale/revoke/completion-fault handling.

The real cross-repository integration gate remains pinned read-only to merged HybridCPU neutral DMA-visibility commit `9e001bf29df06ad3d4ff7337f81d4e5bc0a62fc9` (tree-identical to the previously pinned PR head `4960e7be34f485cf3c8261801daecdefe6172701`). HybridCPU remains v2 / visibility-only. Slices 6–8 use faithful SingNextOS provider models for submit, completion and lifecycle acceptance and do not modify external repositories. Phase-5 completion therefore does **not** claim real HybridCPU executable DMA enablement.

## Goal

Turn local device/MMIO/IRQ/DMA capabilities and exact owned-region authority into bounded, revocable platform effects without giving drivers ambient authority.

The central executable-DMA rule remains:

```text
client-derived exact region authority
AND device-service authority
AND live platform domain/mapping
AND explicit device-memory visibility ordering
AND provider DMA admission/execution
  -> bounded device effect
```

A driver with device authority must never be able to DMA into arbitrary caller memory.

## Slice 1 — exact device lease authority root

A semantic device lifetime is materialized only from:

```text
exact ProcessHandle generation
+ exact PlatformDomainBinding subject/generation
+ live CapabilityId
+ ResourceKind.Device
+ exact semantic ResourceId
+ requested Read / Write / Configure rights
-> separate PlatformDeviceLease identity
```

`CapabilityId` is local admission authority only. Provider and HybridCPU device leases remain separate bridge-private identity spaces.

Device authority must close before the platform domain. Provider closure failure is fail-closed and prevents local reclaim.

## Slice 2 — exact bounded MMIO lease/range

A local `ResourceKind.MmioRegion` capability canonically encodes:

```text
semantic device resource id
+ semantic MMIO region id
+ authoritative semantic byte length
```

The caller supplies only exact relative offset/length and Read/Write access. Admission requires `Map`, matching local Read/Write rights, matching device resource, and device `Configure` plus matching device access rights.

No caller-provided physical MMIO base address exists. The caller therefore cannot widen the authoritative region extent by supplying a larger address/length tuple.

The MMIO identity spaces remain distinct:

```text
CapabilityId
PlatformMmioLeaseId / generation
PlatformProviderMmioLeaseId / generation
NeutralMmioLeaseHandle / epoch
```

MMIO closes before device/domain authority. Exact closure remains structurally possible after local authorization is revoked, while consuming MMIO authority still requires live authorization.

The real HybridCPU provider advertises MMIO contract v1 as `Executable`.

## Slice 3 — exact stale-safe IRQ/event binding

Slice 3 composes exact device authority with a separate semantic IRQ capability and a local process-generation-bound event endpoint. It deliberately exposes no raw interrupt-controller identity.

### Canonical IRQ capability identity

`CapabilityResourceIds.Irq(...)` encodes:

```text
semantic device resource id
+ semantic interrupt source id
+ Edge | Level trigger behavior
```

The runtime accepts no raw vector, APIC/GIC route, MSI/MSI-X number, GSI, controller identity, physical address or provider token.

Admission requires:

```text
exact live ProcessHandle
+ exact live PlatformDeviceLease
+ device Configure authority
+ live ResourceKind.Irq CapabilityId
+ CapabilityRights.Signal
+ capability device id == device lease device id
+ canonical semantic source + trigger
+ exact live KernelEventEndpoint owned by the same ProcessHandle generation
-> separate PlatformIrqBinding
```

All local admission failures happen before provider interrupt binding.

### Separate identity spaces

The route keeps local, provider and HybridCPU identities distinct:

```text
CapabilityId
KernelEventEndpointId / generation
PlatformDeviceLeaseId / generation
PlatformIrqBindingId / generation
PlatformProviderDeviceLeaseId / generation
PlatformProviderIrqBindingId / generation
NeutralDeviceLeaseHandle / epoch
NeutralInterruptLeaseHandle / epoch
NeutralInterruptDeliverySequence
```

The Sing-visible `PlatformIrqBinding` contains only local binding identity, local device lease, semantic source/trigger and local event endpoint. Provider delivery sequence remains bridge-private.

### Policy-neutral local event primitive

The slice adds a small local kernel event mailbox:

```text
KernelEventEndpoint(
    local endpoint id / generation,
    exact ProcessHandle owner)
```

Each endpoint admits at most one pending event in this first slice. `KernelEvent` contains only local endpoint/sequence, a policy-neutral event class and semantic source identity.

The primitive is intentionally not device-specific and can be reused by later timer/runtime/completion work without importing hardware routing identity.

### Delivery ordering

The external delivery path is:

```text
exact live IRQ binding
-> provider poll
-> exact pending provider delivery evidence
-> validate exact live ProcessHandle generation + KernelEventEndpoint
-> publish local KernelEvent
-> complete exact provider delivery sequence
```

Important correctness rules:

- an Exiting or stale process cannot accept a new delivery;
- endpoint owner/generation is validated before provider polling, so an old route cannot deliver into a recycled process generation;
- if the local endpoint is full, no provider completion occurs and the external delivery remains pending;
- if provider completion fails after local publication, the exact just-published local event is synchronously rolled back;
- provider sequence/evidence is not local authority and never appears in SIP-facing state.

### Edge / level semantics

`Edge` and `Level` are semantic trigger behavior carried by the exact source identity. Hardware vector/controller acknowledgment remains provider-private.

`CompleteInterruptDelivery` means that the exact provider-to-kernel semantic delivery was accepted into the local kernel event endpoint. Device-specific register clearing or protocol acknowledgment remains the responsibility of the device service/protocol and is not modeled as a raw interrupt-controller operation.

### Revocation and teardown

Normal authority ordering is:

```text
local IRQ authorization revoked
-> exact provider IRQ binding close
-> exact HybridCPU neutral interrupt route close
-> device authority may close
-> platform domain may close
-> local event endpoint/process reclaim
```

Rules enforced by the slice:

- IRQ capability revoke closes only routes derived from that capability;
- explicit device revoke closes dependent IRQ routes before MMIO/device close;
- device-capability revoke closes dependent IRQ routes before the device lease;
- explicit event-endpoint close first closes all routes targeting that exact endpoint;
- process teardown marks IRQ authorization revoked and closes routes before device/domain closure;
- event endpoints are reclaimed only after external route/device/domain authority is closed;
- IRQ provider-close failure pins teardown in `PlatformFaulted` and forbids device/domain close and local reclaim;
- the HybridCPU provider independently refuses device close while a provider IRQ binding is live;
- the neutral runtime independently refuses device close while a neutral interrupt route is live and drops pending semantic delivery when the route itself closes.

### HybridCPU neutral interrupt owner

The narrow neutral owner materializes:

```text
NeutralInterruptLease(
    exact live NeutralDeviceLease,
    bounded semantic source identity,
    Edge | Level)
```

It provides explicit semantic signal/poll/complete/close behavior with one exact pending delivery sequence. Stale or forged lease/sequence identities fail closed.

This surface exports no vector/controller/APIC/GIC/MSI/GSI, DMA, IOMMU, physical-address, VM, queue, lane or opcode authority.

## Slice 4 — exact admission-only DMA grant authority

Slice 4 introduces a bounded, revocable DMA **admission** authority without introducing a transfer-execution surface.

### Authority composition

A Sing-local grant is materialized only from already-proven exact authorities:

```text
exact live ProcessHandle generation
+ exact live PlatformDeviceLease
+ exact live PlatformOwnedRegionSliceMapping
+ exact bounded range relative to that mapped slice
+ DeviceReadsMemory | DeviceWritesMemory | Bidirectional
-> separate PlatformDmaGrant identity
```

There is no new DMA capability namespace. Device capability authority is already committed into the exact `PlatformDeviceLease`; memory capability and `RegionAuthority` ownership are already committed into the exact mapping. `RegionAuthority` remains the sole ownership authority.

The DMA identity spaces remain separate:

```text
PlatformDmaGrantId / generation
PlatformProviderDmaGrantId / generation
NeutralDmaGrantHandle / epoch
```

Provider and HybridCPU identities stay bridge-private and never become SIP/local memory authority.

### Admission rules

Admission requires:

- exact live device and mapping identities;
- device and mapping belonging to the exact same platform-domain lifetime;
- a positive non-overflowing range wholly contained in the exact mapped slice;
- device `Configure` plus direction-specific `Read`/`Write` rights;
- mapped-memory access matching the direction;
- one live grant per exact mapping in this first admission-only slice.

`DeviceReadsMemory` requires readable device/mapping authority; `DeviceWritesMemory` requires writable device/mapping authority; `Bidirectional` requires both.

Missing/forged/stale local authority and invalid range/direction/access fail before the provider DMA-grant call.

### Admission is not execution

A successful `PlatformDmaGrant` proves only that the exact device and exact bounded mapped range are composition-compatible for DMA authority. It does **not** prove:

- CPU-to-device publication or cache maintenance;
- transfer submission;
- hardware/device execution;
- completion;
- device-to-CPU acquisition/maintenance;
- coherent DMA.

At the Slice-4 boundary the real HybridCPU provider advertised:

```text
PlatformFeatureFamily.DmaMapping
  -> PlatformDmaGrantContract v1 / RuntimeAdmission
```

It was explicitly **not** `Executable`.

### Lifetime and teardown ordering

A live grant pins both lower authorities:

```text
live DMA grant
  -> device close denied
  -> mapped-region close denied
```

Runtime teardown closes dependent DMA grants before lower authority:

```text
DMA grant close
-> IRQ/MMIO/device closure as applicable
-> region-mapping closure
-> domain/process reclaim
```

The slice enforces this ordering for explicit device revoke, explicit region-mapping revoke, device-capability cascade, memory-capability cascade, and process teardown. If DMA-grant revoke faults, lower device/mapping authority remains pinned and reclaim is forbidden.

Provider and neutral layers independently enforce the same dependent-lifetime rule rather than trusting only the Sing runtime ordering.

### Hardware-boundary exclusions

The public DMA admission surfaces expose no:

- raw/physical/bus address;
- IOMMU identifier or control handle;
- page-table/PTE identity;
- descriptor, scatter/gather, ring or queue identity;
- interrupt vector/controller identity;
- VM state, lane or opcode identity.

There is no `SubmitDma`, DMA completion, or transfer queue API in Slice 4.

## Slice 5 — exact grant-scoped non-coherent DMA visibility

Slice 5 adds explicit memory-visibility ordering to the exact DMA grant while deliberately keeping DMA execution out of scope.

### Visibility cycle identity

Prepare materializes a fresh visibility cycle rooted in the exact live grant:

```text
exact live PlatformDmaGrant
-> PreparePlatformDmaForDevice
-> fresh PlatformDmaVisibilityCycle
-> hidden PlatformProviderDmaVisibilityCycle
-> hidden NeutralDmaVisibilityCycle
-> release/publication evidence
```

The three cycle identity spaces are separate. Provider and HybridCPU cycle tokens remain bridge-private and never become local authority.

The Sing-visible prepare evidence carries only:

```text
exact local DMA grant id / generation
+ exact local visibility cycle
+ exact DMA direction
+ PublicationFence requirement
+ PublicationFenceSatisfied outcome
```

The evidence is **not authority**, **not submission evidence**, and **not completion evidence**.

### Non-coherent prepare semantics

Every current neutral owned-region mapping remains explicitly `NonCoherent`. Prepare therefore requires an explicit publication/release boundary for the exact mapping behind the exact grant.

The neutral implementation composes the grant with the already policy-neutral owned-region visibility primitive and requires:

```text
NeutralMemoryVisibilityRequirement.PublicationFence
-> NeutralMemoryVisibilityOutcome.PublicationFenceSatisfied
```

A successful prepare starts a fresh cycle and leaves the grant, mapping, device and domain authority live. It proves only CPU-to-device visibility ordering for a possible future operation.

### Direction-aware CPU acquire

Post-write acquire is meaningful only when the device may have written memory:

```text
DeviceWritesMemory | Bidirectional
+ exact prepared visibility cycle
-> AcquirePlatformDmaForCpu
-> AcquisitionFenceSatisfied evidence
```

`DeviceReadsMemory` cannot modify memory and therefore has no post-write CPU acquire requirement; attempting the acquire path is denied as not required.

For write-capable directions, acquire before prepare is denied. A second acquire for the same cycle is denied. A successful acquire consumes the current cycle for future-submit purposes while leaving the grant and mapping themselves live.

### Future-submit-safe cycle consumption

The cycle state deliberately distinguishes:

```text
prepared + not acquired
prepared + acquired
```

A future executable-DMA submit slice can therefore require **prepared + not acquired**. This prevents a caller from acquiring before submission and then reusing that old acquire evidence as if it proved post-completion CPU visibility.

Re-prepare after an acquire creates a fresh local/provider/neutral cycle. This is the bridge between the current visibility-only slice and a later submit/completion state machine without pretending that completion already exists.

### Fail-closed evidence validation

The slice validates evidence at every namespace boundary:

- stale or forged local DMA grant fails before any provider visibility call;
- provider prepare evidence must match exact provider grant id/generation, direction and a materialized provider cycle;
- provider acquire evidence must match the exact stored provider cycle from prepare;
- HybridCPU prepare/acquire evidence must match the exact neutral grant handle/epoch, direction and exact stored neutral cycle;
- malformed provider evidence becomes `PlatformFaulted`;
- revoked grant cannot create new visibility evidence.

### No completion or ownership claim

Acquire in Slice 5 is only a visibility fence operation for the exact cycle. Because no device operation could yet be submitted at the Slice-5 boundary, it did **not** prove that a device transfer occurred or completed.

`RegionAuthority` remains the sole ownership authority. The slice does not return a region to CPU ownership, does not alter region generation, and does not weaken the existing rule that lower device/mapping authority must close before reclaim where required.

## Slice 6 — bounded DMA submit on the exact prepared cycle

Slice 6 adds bounded **submission acceptance** while deliberately excluding completion proof.

### Exact submit admission

Submission requires both live authority and current phase evidence:

```text
exact live PlatformDmaGrant
+ satisfied PlatformDmaPrepareEvidence for that exact grant generation
+ evidence cycle == current local visibility cycle
+ current cycle prepared && not acquired
+ DmaMapping contract v3 / RuntimeAdmission
-> hidden provider grant + hidden provider visibility cycle
-> provider bounded submit acceptance
-> fresh Sing-local PlatformDmaOperationId / generation
-> pending submitted lifetime
```

The transfer bound is exactly the range and direction already committed by the grant. There is no caller-provided raw address, descriptor, scatter/gather list, queue/ring identity, IOMMU identity, PTE identity, interrupt-controller identity, or provider execution token.

`PlatformDmaPrepareEvidence` remains evidence, not authority. It cannot submit anything without the exact live grant and exact current bridge state.

### Phase safety

The submit state machine now distinguishes:

```text
prepared + not acquired + not submitted
prepared + acquired
submitted + completion not proven
fault-pinned ambiguous submission
```

Rules enforced by Slice 6:

- submit without prepare is denied before provider submit;
- stale grant generation fails before provider submit;
- forged or replayed prepare cycle fails before provider submit;
- an acquired/pre-consumed cycle cannot be submitted;
- re-prepare cannot overwrite an already-submitted cycle;
- a second submit cannot reuse a pending cycle;
- write-side `AcquirePlatformDmaForCpu` is denied while the submitted operation is pending, so pre-completion acquire cannot become post-completion evidence;
- a successful submit creates only a pending operation identity and is never completion evidence.

### Pending-lifetime pinning

A submitted operation pins all dependent lower authority:

```text
pending DMA submission
-> DMA grant close denied as draining
-> region-mapping close denied
-> device close denied
-> platform domain / process reclaim denied
```

Local capability revoke still stops **new** effects immediately. If capability revoke races an already-submitted operation, local device authorization becomes revoked, but the existing DMA grant remains structurally tracked and pinned until a later completion/drain slice proves closure.

Process teardown treats a normal pending submission as `PlatformDraining`, not `PlatformFaulted`, and does not attempt lower mapping/domain closure. If submit returned `Faulted`, or reported success with malformed provider submission identity/cycle/range/direction, the grant becomes fault-pinned because the external effect may have been accepted ambiguously; teardown stops at `PlatformFaulted` before touching lower authority.

### Separate identity spaces

The local submission surface contains only:

```text
PlatformDmaOperationId / generation
exact local DMA grant id / generation
exact local visibility cycle
exact grant-bounded range and direction
```

Provider submission identity and provider visibility-cycle identity remain bridge-private. The real HybridCPU/neutral provider has not been advanced for this slice.

### Completion is explicitly out of scope

There is no DMA completion API in Slice 6. A `PlatformDmaSubmission` proves only that the bounded operation was accepted into an external pending lifetime. It does **not** prove:

- device execution completed;
- device-written bytes are visible to CPU;
- CPU may acquire or reuse the buffer;
- DMA grant/mapping/device authority may close;
- `RegionAuthority` ownership may be reclaimed.

The next slice must introduce exact completion proof for the exact pending local/provider operation and exact prepared cycle without allowing stale, forged, replayed, wrong-generation, or wrong-cycle evidence to advance the state machine.

## Feature discovery after Slice 6

The real pinned HybridCPU provider remains:

```text
PlatformFeatureFamily.IoDomainBinding -> device lease v1 / Executable
PlatformFeatureFamily.MmioMapping     -> MMIO lease v1 / Executable
PlatformFeatureFamily.IrqBinding      -> IRQ binding v1 / Executable
PlatformFeatureFamily.DmaMapping      -> DMA grant + visibility v2 / RuntimeAdmission
```

The faithful SingNextOS submit model advertises:

```text
PlatformFeatureFamily.DmaMapping
  -> PlatformDmaSubmissionContract v3 / RuntimeAdmission
```

`RuntimeAdmission` remains intentionally weaker than `Executable`. v3 means bounded submit acceptance on the exact prepared/unacquired cycle; it still does not advertise completion proof or a complete executable DMA path.

## Tests

Slice-1 through Slice-5 tests remain in place.

Slice-6 focused tests prove:

- exact prepared/unacquired grant state can submit exactly the grant-bounded range and direction;
- submission yields a fresh local operation identity while provider submission tokens remain hidden;
- submit without prepare fails before provider submit;
- stale grant, forged cycle, replayed old cycle, and pre-acquired cycle fail before provider submit;
- a submitted cycle blocks re-prepare, second submit, write-side acquire, DMA-grant revoke, mapping revoke and device revoke;
- local device-capability revoke stops new submission while the already-submitted operation remains pinned;
- process teardown stays `PlatformDraining` and does not reclaim mapping/device/domain state while submission is pending;
- malformed provider-success evidence and provider `Faulted` submit fail closed and fault-pin lower authority;
- an ordinary provider denial does not consume the prepared cycle and can be retried;
- public local submission state exposes no provider/neutral token, raw address, IOMMU/PTE, descriptor/queue, interrupt-controller, VM/lane/opcode, or completion identity;
- v3 is `RuntimeAdmission`, not `Executable`, and no DMA completion API exists;
- the unchanged pinned HybridCPU v2 visibility integration continues to pass.

## Slice 7 — exact DMA completion proof

Slice 7 adds a distinct completion-proof boundary for the exact pending DMA operation without coupling that proof to CPU acquire or authority closure.

### Exact completion identity

Completion observation starts from the exact tracked local submission and validates the same operation/cycle across separate identity namespaces:

```text
exact PlatformDmaSubmission
+ exact local operation id / generation
+ exact local DMA grant id / generation
+ exact local prepared visibility cycle
-> hidden exact PlatformProviderDmaSubmission
-> provider completion observation
-> exact provider submission id / generation
+ exact provider grant id / generation
+ exact provider prepared visibility cycle
+ exact submitted range / direction
-> one-shot PlatformDmaCompletionEvidence
```

The local completion evidence contains only Sing-local operation/grant/cycle identities plus the bounded range/direction. Provider execution identities never become local authority or SIP-visible state.

### Pending, completed, and faulted states

The completion state machine distinguishes:

```text
submitted + completion pending
submitted + exact completion proven
submitted + completion fault-pinned
```

`Pending` returns `PlatformBindingDraining` and produces no completion evidence. `Completed` marks the exact tracked operation as completion-proven and creates one local completion evidence value. A second observation of that completed operation is rejected before another provider call, so completed evidence cannot be replayed to advance the state machine twice.

Provider `Faulted`, stale/revoked/wrong-domain completion lifetime, or a provider-success response whose submission/grant generation, prepared cycle, range, or direction does not match the exact stored submission becomes `PlatformFaulted` and keeps lower authority pinned.

### Local stale/forged evidence rejection

Before any provider completion call, the runtime rejects:

- wrong local operation identity;
- stale local operation generation;
- stale local DMA-grant generation;
- wrong local prepared visibility cycle;
- malformed local range/direction state;
- wrong process/domain generation.

Completion observation deliberately uses identity-only validation rather than live local capability authorization. This allows an already-authorized DMA effect to drain after capability revoke and while its process is `Exiting`; it does not authorize any new device effect.

### Completion proof is not acquire or reclaim

A successful `PlatformDmaCompletionEvidence` proves only that the exact submitted provider operation for the exact prepared cycle reported completion. It does **not**:

- perform `AcquirePlatformDmaForCpu`;
- prove device-written bytes are CPU-visible;
- consume the post-completion acquire requirement;
- remove the tracked submission lifetime;
- permit a second submit or re-prepare;
- permit DMA-grant, mapping, device, or platform-domain closure;
- permit `RegionAuthority` reclaim or CPU reuse.

The completed submission remains tracked and therefore continues to pin lower authority until the next slice performs the direction-aware post-completion acquire/maintenance and explicit lifecycle continuation.

### Feature discovery after Slice 7

The real pinned HybridCPU provider remains visibility-only v2 / `RuntimeAdmission`. The faithful SingNextOS completion model advertises:

```text
PlatformFeatureFamily.DmaMapping
  -> PlatformDmaCompletionContract v4 / RuntimeAdmission
```

v4 remains intentionally **not** `Executable`: completion proof exists, but the full DMA reuse/closure path is not yet complete.

### Slice-7 focused tests

Slice-7 tests prove:

- exact `Pending` completion remains draining and yields no completion proof;
- exact `Completed` evidence is bound to the exact local operation, grant generation, prepared cycle, range and direction;
- stale local operation/grant generations, forged operation identity, wrong cycle and malformed local submission state fail before provider completion observation;
- wrong provider submission id/generation, grant id/generation, prepared cycle, range or direction fault-pins lower authority;
- provider stale/revoked/wrong-domain/faulted completion lifetime fails closed;
- exact completion evidence is one-shot and replay is denied before another provider call;
- completion observation still works after device-capability revoke and while the process is `Exiting`;
- completion proof still leaves acquire, DMA revoke, mapping/device/domain closure and local reclaim pinned;
- public completion evidence contains no provider/neutral token, raw address, IOMMU/PTE, descriptor/queue, interrupt-controller, VM/lane/opcode identity;
- unchanged pinned HybridCPU visibility integration remains green.

## Slice 8 — post-completion visibility and controlled release to CPU reclaim

Slice 8 closes the remaining post-submit lifecycle without introducing coherent-DMA assumptions or evidence-as-authority shortcuts.

### Direction-aware post-completion visibility

The exact completed operation must advance through its exact consumed prepared cycle:

```text
exact tracked PlatformDmaSubmission
+ exact satisfied PlatformDmaCompletionEvidence
+ completion-proven bridge state
+ exact consumed PlatformDmaVisibilityCycle
-> DeviceReadsMemory:
     post-completion visibility = NotRequired
-> DeviceWritesMemory | Bidirectional:
     exact provider acquire for the stored provider cycle
     -> AcquisitionFenceSatisfied
-> PlatformDmaPostCompletionVisibilityEvidence
```

For device-read-only DMA, the device cannot have modified memory, so no CPU acquire is issued. For write-capable DMA, a **fresh acquire after exact completion** is mandatory. An acquire performed before submit belongs to a consumed earlier cycle and cannot satisfy the post-completion requirement.

Successful submit permanently marks its exact prepared cycle consumed. Removing the submitted-operation pin after completion therefore cannot make that old cycle eligible for replay submission.

### Evidence is not release authority

`PlatformDmaPostCompletionVisibilityEvidence` contains only local operation/grant/cycle identities, direction, and semantic visibility requirement/outcome. It carries no provider token, raw address, IOMMU/PTE, descriptor/queue, interrupt-controller, VM/lane/opcode or backend execution identity.

Completion and post-completion visibility evidence do **not** themselves release memory ownership. They only permit the existing explicit authority closure sequence to continue.

### Controlled release ordering

The completed operation remains a lower-authority pin until required post-completion visibility succeeds. The release path is:

```text
exact completion proven
-> required post-completion visibility satisfied
-> submitted-operation lifetime retired
-> exact DMA grant revoke
-> completion-backed exact region-mapping revoke reaches Closed
-> local platform-mapping reservation released
-> device/domain closure as applicable
-> RegionAuthority transfer/reclaim allowed
```

The provider mapping close remains completion-backed. A legacy synchronous provider revoke is not sufficient to release `PlatformMappingReserved`; verified `Closed` evidence is required before local reservation release.

`RegionAuthority` remains the only component that changes CPU ownership/generation or final process reclaim state.

### Revocation and process teardown

Local capability revoke continues to stop new effects immediately. Exact completion observation and post-completion visibility remain available for the already-authorized operation after local revoke or while the process is `Exiting` so that external authority can drain safely.

Process teardown remains `PlatformDraining` before exact completion/post-visibility. Once those phases complete, existing teardown ordering closes DMA grants, devices and completion-backed mappings, closes the domain, releases the mapping reservation, and only then calls local region reclaim. Any DMA completion or post-completion visibility fault leaves teardown `PlatformFaulted` and lower authority pinned.

### Fail-closed validation

Slice 8 rejects or pins:

- fabricated completion evidence presented before bridge completion is proven;
- stale local operation generation;
- stale local grant generation;
- wrong local prepared visibility cycle;
- replayed consumed prepared cycles;
- pre-submit acquire used as a substitute for post-completion acquire;
- malformed provider post-completion acquire grant/cycle/direction evidence;
- provider stale/revoked/wrong-domain/faulted acquire lifetime;
- mapping reclaim before completion-backed mapping closure reaches `Closed`.

### Feature discovery after Slice 8

The faithful SingNextOS lifecycle model advertises:

```text
PlatformFeatureFamily.DmaMapping
  -> PlatformDmaLifecycleContract v5 / RuntimeAdmission
```

The real pinned HybridCPU provider remains:

```text
PlatformFeatureFamily.DmaMapping
  -> DMA grant + visibility v2 / RuntimeAdmission
```

Phase-5 correctness acceptance is therefore satisfied by the faithful v5 model while real HybridCPU executable-DMA enablement remains separate future provider/backend work.

### Slice-8 focused and acceptance tests

Slice-8 tests prove:

- device-write DMA cannot revoke its grant after completion until fresh post-completion acquire succeeds;
- device-read-only DMA performs no acquire but still consumes the submitted cycle before release;
- stale operation/grant generation and wrong-cycle completion evidence fail before provider acquire;
- fabricated completion evidence cannot advance post-completion visibility before exact bridge completion;
- pre-submit acquire cannot satisfy a later submitted cycle's post-completion acquire;
- malformed provider acquire evidence fault-pins DMA grant, mapping/device closure and CPU transfer;
- local capability revoke blocks new effects while exact completion/post-visibility drain remains possible;
- completion-backed mapping closure is required before `RegionAuthority` releases the platform mapping reservation;
- explicit successful ordering reaches DMA grant closure, mapping/device/domain closure and CPU `TransferRegion` with a fresh region generation;
- process termination remains draining until completion/post-visibility, then reaches platform closure and local region `Released` reclaim;
- completion-provider fault injection leaves the process `PlatformFaulted`, lower provider authority unclosed and the region still `Owned`;
- public lifecycle evidence contains no provider/neutral or hardware/backend identity;
- unchanged pinned HybridCPU v2 visibility integration remains green.

## DMA — Phase-5 acceptance result

The bounded DMA state machine is now proven end to end by a faithful provider model:

```text
CPU-Owned exact region/mapping
-> exact DMA grant admission
-> Prepare / Publish -> fresh cycle
-> Submit bounded transfer using that exact prepared cycle
-> exact completion proven for that exact operation/cycle
-> Acquire / maintenance when device may have written memory
-> revoke exact DMA grant
-> verified completion-backed region-mapping closure
-> device/domain closure as applicable
-> CPU ownership transfer / process reclaim allowed by RegionAuthority
```

No coherent-DMA premise is used. Grant existence, prepare evidence, submit acceptance or completion proof alone never authorizes CPU reuse.

## Acceptance criteria

Phase 5 required one real or faithful provider path to perform a bounded DMA lifecycle over an owned region and prove:

```text
owned exact region
-> exact grant
-> prepare/publication
-> bounded submit
-> exact completion proof
-> post-write acquire when required
-> DMA/mapping/device closure
-> CPU reuse/reclaim
```

**This acceptance criterion is met by the faithful SingNextOS v5 provider path. Phase 5 is Complete.**

Real HybridCPU executable-DMA implementation is intentionally not implied by this status and remains future provider/backend integration work.

## Remaining Phase-5 work

None for the Phase-5 correctness acceptance boundary.

## Do not do

- no raw physical MMIO address ABI;
- no raw interrupt vector/controller ABI;
- no raw DMA pointer ABI;
- no app-visible IOMMU IDs;
- no ambient global device service authority over arbitrary memory;
- no assumption of coherent DMA;
- no universal driver DSL as a prerequisite.
