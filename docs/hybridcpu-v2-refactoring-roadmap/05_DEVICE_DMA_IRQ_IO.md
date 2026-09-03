# Phase 5 — Device, MMIO, IRQ and DMA authority

## Status

**In progress — five vertical slices implemented.**

Implemented slices:

1. exact capability-backed semantic device lease authority;
2. exact capability-backed bounded MMIO lease/range authority;
3. exact capability-backed stale-generation-safe IRQ/event binding;
4. exact admission-only DMA grant authority composed from a live device lease plus an exact mapped owned-region slice, bounded relative range, and direction;
5. exact grant-scoped non-coherent DMA visibility cycles with release/publication prepare evidence and direction-aware post-write CPU acquire evidence.

Slice 5 closes the explicit non-coherent visibility-ordering step without claiming executable DMA. DMA submit, completion/drain/revoke ordering around an in-flight transfer, and a real or faithful bounded transfer acceptance path remain Phase-5 work. The phase acceptance boundary is therefore **not** complete.

The Slice-5 cross-repository integration gate is pinned to HybridCPU neutral DMA-visibility commit `4960e7be34f485cf3c8261801daecdefe6172701`, based on HybridCPU `master` `1c0adf62fb01bc0963caa2dacc5f0933a8bce8cc` after the admission-only DMA-grant slice.

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

Acquire in Slice 5 is only a visibility fence operation for the exact cycle. Because no device operation can yet be submitted, it does **not** prove that a device transfer occurred or completed.

`RegionAuthority` remains the sole ownership authority. The slice does not return a region to CPU ownership, does not alter region generation, and does not weaken the existing rule that lower device/mapping authority must close before reclaim where required.

## Feature discovery after Slice 5

The real HybridCPU provider advertises:

```text
PlatformFeatureFamily.IoDomainBinding -> device lease v1 / Executable
PlatformFeatureFamily.MmioMapping     -> MMIO lease v1 / Executable
PlatformFeatureFamily.IrqBinding      -> IRQ binding v1 / Executable
PlatformFeatureFamily.DmaMapping      -> DMA grant + visibility v2 / RuntimeAdmission
```

`RuntimeAdmission` remains intentionally weaker than `Executable`. Contract v2 means exact DMA grant admission plus exact grant-scoped non-coherent prepare/acquire visibility cycles; it does not advertise transfer execution or completion.

## Tests

Slice-1 through Slice-4 tests remain in place.

Slice-5 focused tests prove:

- an exact live DMA grant can create a fresh local/provider/neutral visibility cycle only through explicit publication preparation;
- prepare evidence is bound to the exact grant generation, direction and cycle;
- write-capable DMA acquire is denied before prepare;
- read-only device DMA does not require a post-write acquire;
- acquire evidence must refer to the exact most-recent prepared cycle;
- a cycle cannot be acquired twice;
- re-prepare after acquire creates a fresh cycle, so pre-acquire cannot satisfy a future submit requirement;
- stale or forged local DMA grants fail before provider visibility calls;
- malformed provider prepare/acquire evidence fails closed as `PlatformFaulted`;
- revoked neutral grants cannot produce visibility evidence;
- prepare/acquire leave the exact grant and mapped-region authority live;
- real pinned HybridCPU integration executes the full Sing -> provider -> neutral prepare/acquire path;
- local/provider/neutral visibility-cycle identity types remain distinct;
- public visibility evidence contains no provider/Hybrid token, raw address, IOMMU, page-table, descriptor/queue, interrupt-controller, VM, lane/opcode, submission or completion identity;
- feature discovery reports DMA visibility v2 as `RuntimeAdmission`, not `Executable`.

## DMA — remaining Phase-5 work

Slices 4 and 5 now establish exact DMA admission plus explicit non-coherent visibility ordering. Executable DMA must add bounded submission and completion semantics on top of a **prepared, not-yet-acquired** exact visibility cycle:

```text
CPU-Owned exact region/mapping
-> exact DMA grant admission
-> Prepare / Publish -> fresh cycle
-> Submit bounded transfer using that exact prepared cycle
-> completion pending
-> completion proven for that exact operation/cycle
-> Acquire / maintenance when device may have written memory
-> revoke DMA grant
-> revoke region mapping
-> CPU ownership / reclaim allowed
```

No coherent-DMA premise is allowed. Grant existence or visibility evidence alone must never authorize CPU reuse or claim device completion.

Required remaining negative/acceptance tests include:

- submit without an exact prepared + unacquired visibility cycle is denied;
- an acquired/pre-consumed visibility cycle cannot be submitted;
- device-written memory cannot return to CPU use before exact completion plus required post-write acquire/maintenance;
- local capability revoke stops new submissions immediately while an already-submitted DMA operation drains;
- process termination cannot reclaim the buffer before DMA completion, required acquire/maintenance, and grant/mapping closure;
- stale/faulted provider execution/completion evidence fails closed;
- provider execution tokens never appear in SIP payloads;
- one real or faithful bounded DMA path survives denial/stale/revoke/completion fault injection.

## Acceptance criteria

Phase 5 is complete only when one real or faithful provider path performs a bounded DMA transfer over an owned region and proves that the buffer is not reclaimed or returned to CPU ownership before completion, required acquire/maintenance, and device/DMA authority closure.

**That acceptance criterion is not yet met. Phase 5 remains In progress.**

## Remaining Phase-5 work

- bounded DMA submit plus exact completion/drain/revoke ordering and process-teardown integration;
- one real or faithful bounded DMA acceptance path with denial/stale/revoke/completion fault injection.

## Do not do

- no raw physical MMIO address ABI;
- no raw interrupt vector/controller ABI;
- no raw DMA pointer ABI;
- no app-visible IOMMU IDs;
- no ambient global device service authority over arbitrary memory;
- no assumption of coherent DMA;
- no universal driver DSL as a prerequisite.
