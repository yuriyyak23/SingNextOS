# Phase 5 — Device, MMIO, IRQ and DMA authority

## Status

**In progress — four vertical slices implemented.**

Implemented slices:

1. exact capability-backed semantic device lease authority;
2. exact capability-backed bounded MMIO lease/range authority;
3. exact capability-backed stale-generation-safe IRQ/event binding;
4. exact admission-only DMA grant authority composed from a live device lease plus an exact mapped owned-region slice, bounded relative range, and direction.

Slice 4 closes the first DMA-authority admission step, including exact identity composition and dependent-lifetime ordering. It deliberately does **not** claim executable DMA: non-coherent prepare/acquire, submit, completion/drain, and a real or faithful bounded transfer acceptance path remain Phase-5 work. The phase acceptance boundary is therefore **not** complete.

The Slice-4 cross-repository integration gate is pinned to HybridCPU neutral DMA-grant commit `ccb1dc3d9b35beedfe4286e010c51451e8a46f78`, based on HybridCPU `master` `67d1e6f528de2f181c4b5c68df4e95ef7e2bd0aa` after the IRQ/event slice.

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

Accordingly, the real HybridCPU provider advertises:

```text
PlatformFeatureFamily.DmaMapping
  -> PlatformDmaGrantContract v1 / RuntimeAdmission
```

It is explicitly **not** `Executable`.

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

## Feature discovery after Slice 4

The real HybridCPU provider advertises:

```text
PlatformFeatureFamily.IoDomainBinding -> device lease v1 / Executable
PlatformFeatureFamily.MmioMapping     -> MMIO lease v1 / Executable
PlatformFeatureFamily.IrqBinding      -> IRQ binding v1 / Executable
PlatformFeatureFamily.DmaMapping      -> DMA grant v1 / RuntimeAdmission
```

`RuntimeAdmission` is intentionally weaker than `Executable` and prevents discovery from overstating the implemented DMA behavior.

## Tests

Slice-1, Slice-2 and Slice-3 tests remain in place.

Slice-4 focused tests prove:

- exact live device lease plus exact live mapped region slice materialize a separate bounded DMA grant;
- device and mapping must belong to the same exact domain lifetime;
- invalid range, undefined direction, insufficient device rights and insufficient mapped-memory access are rejected;
- missing/forged local device or mapping authority fails before the provider DMA-grant call;
- one live grant per exact mapping is enforced in this first slice;
- stale or forged grant closure fails closed;
- a live grant blocks both device and mapping closure at runtime, provider and neutral layers;
- explicit revoke, capability cascade and process teardown drain DMA grants before lower device/mapping authority;
- DMA-grant revoke failure prevents lower authority closure;
- the real pinned HybridCPU provider materializes and closes the exact neutral DMA grant;
- public Sing/provider/neutral DMA surfaces contain no raw address, IOMMU, page-table, descriptor/queue, VM, lane or opcode authority;
- the facade contains no DMA submit/completion operation;
- feature discovery reports DMA grant v1 as `RuntimeAdmission`, not `Executable`.

## DMA — remaining Phase-5 work

Slice 4 establishes admission only. Executable DMA must add explicit visibility and completion semantics on top of that exact grant:

```text
CPU-Owned exact region/mapping
-> exact DMA grant admission
-> Prepare / Publish for device direction
-> Submit bounded transfer
-> completion pending
-> completion proven
-> Acquire / maintenance when device may have written memory
-> revoke DMA grant
-> revoke region mapping
-> CPU ownership / reclaim allowed
```

No coherent-DMA premise is allowed. Grant existence alone must never authorize submission or CPU reuse.

Required remaining negative/acceptance tests include:

- non-coherent device access without required prepare/publish is denied before submit;
- device-written memory cannot return to CPU use before completion plus required acquire/maintenance;
- local capability revoke stops new submissions immediately while an already-submitted DMA operation drains;
- process termination cannot reclaim the buffer before DMA completion, required acquire/maintenance, and grant/mapping closure;
- stale/faulted provider execution/completion evidence fails closed;
- provider execution tokens never appear in SIP payloads;
- one real or faithful bounded DMA path survives denial/stale/revoke/completion fault injection.

## Acceptance criteria

Phase 5 is complete only when one real or faithful provider path performs a bounded DMA transfer over an owned region and proves that the buffer is not reclaimed or returned to CPU ownership before completion, required acquire/maintenance, and device/DMA authority closure.

**That acceptance criterion is not yet met. Phase 5 remains In progress.**

## Remaining Phase-5 work

- explicit non-coherent DMA prepare/publish and post-write acquire/maintenance semantics;
- bounded DMA submit plus completion/drain/revoke ordering and process-teardown integration;
- one real or faithful bounded DMA acceptance path with denial/stale/revoke/completion fault injection.

## Do not do

- no raw physical MMIO address ABI;
- no raw interrupt vector/controller ABI;
- no raw DMA pointer ABI;
- no app-visible IOMMU IDs;
- no ambient global device service authority over arbitrary memory;
- no assumption of coherent DMA;
- no universal driver DSL as a prerequisite.
