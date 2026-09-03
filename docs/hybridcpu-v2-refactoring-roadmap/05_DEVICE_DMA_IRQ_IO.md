# Phase 5 — Device, MMIO, IRQ and DMA authority

## Status

**In progress — three vertical slices implemented.**

Implemented slices:

1. exact capability-backed semantic device lease authority;
2. exact capability-backed bounded MMIO lease/range authority;
3. exact capability-backed stale-generation-safe IRQ/event binding.

The remaining Phase-5 acceptance work is DMA authority, visibility/ownership ordering, completion/drain/revoke, and a bounded DMA acceptance path. The phase acceptance boundary is therefore **not** complete.

The Slice-3 cross-repository integration gate is pinned to HybridCPU neutral IRQ/event commit `cb42afbee49bb632467d9f3c13dc7eb9f96524eb`, based on normalized HybridCPU `master` `53e51234e9428115a9af505549f939d0d4eb4e4b`.

## Goal

Turn local device/MMIO/IRQ/DMA capabilities and exact owned-region authority into bounded, revocable platform effects without giving drivers ambient authority.

The central later-DMA rule remains:

```text
client-derived exact region authority
AND device-service authority
AND live platform domain/mapping
AND HybridCPU I/O-domain/IOMMU admission
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

The real HybridCPU provider advertises MMIO contract v1 as `Executable`. `DmaMapping` remains unavailable.

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

### Feature discovery

The real HybridCPU provider now advertises:

```text
PlatformFeatureFamily.IoDomainBinding -> device lease v1 / Executable
PlatformFeatureFamily.MmioMapping     -> MMIO lease v1 / Executable
PlatformFeatureFamily.IrqBinding      -> IRQ binding v1 / Executable
PlatformFeatureFamily.DmaMapping      -> Unavailable
```

`IrqBinding` is appended after the existing feature families so existing numeric identities are not renumbered.

## Tests

Slice-1 and Slice-2 tests remain in place.

Slice-3 focused tests prove:

- canonical IRQ capability identity round-trips semantic device/source/trigger;
- exact IRQ capability + exact live device lease + exact local event endpoint materialize a separate route;
- wrong device resource, missing `Signal`, non-canonical IRQ identity and wrong endpoint owner fail before provider binding;
- device `Configure` authority is required;
- provider delivery becomes a local `KernelEvent` and exact provider completion occurs only after local publication;
- a full local endpoint leaves external delivery pending and unacknowledged until publication can succeed;
- stale old process handles and recycled process generations cannot receive from an old route;
- IRQ capability revoke closes derived route without closing unrelated device authority;
- device revoke and process teardown close IRQ route before device/domain authority;
- IRQ close fault pins teardown before device/domain close and event reclaim;
- real pinned HybridCPU provider materializes and closes the exact neutral interrupt route;
- neutral signal/poll/complete tests reject stale/forged route and wrong delivery sequence;
- public Sing and neutral IRQ/event surfaces contain no provider or raw hardware-routing authority identities.

## DMA — remaining Phase-5 work

DMA must compose exact device authority with exact caller-derived region authority:

```text
DmaGrantLease BindDma(
    DeviceLease,
    PlatformRegionMappingLease,
    ExactRange,
    Direction)
```

The required lifecycle remains:

```text
CPU-Owned
-> Prepare / Publish
-> exact DMA grant
-> Submit
-> completion pending
-> completion proven
-> Acquire / maintenance
-> revoke DMA grant
-> revoke region mapping
-> CPU ownership / reclaim allowed
```

`Direction` must distinguish device-read, device-write and bidirectional where supported. No coherent-DMA premise is allowed.

Required remaining negative/acceptance tests include:

- device capability without region authority cannot create a DMA grant;
- region authority without device authority cannot create a DMA grant;
- wrong DMA range/direction is denied before provider submit;
- stale domain/IOMMU/provider epoch invalidates the DMA grant;
- non-coherent device access without required prepare/acquire is denied;
- local capability revoke stops new submissions immediately while old DMA drains;
- process termination cannot reclaim the buffer before DMA completion/revoke;
- provider token never appears in a SIP payload;
- one real or faithful bounded DMA path survives denial/stale/revoke fault injection.

## Acceptance criteria

Phase 5 is complete only when one real or faithful provider path performs a bounded DMA transfer over an owned region and proves that the buffer is not reclaimed or returned to CPU ownership before completion, required acquire/maintenance, and device/DMA authority closure.

**That acceptance criterion is not yet met. Phase 5 remains In progress.**

## Remaining Phase-5 work

- exact DMA grant composed from device lease plus exact region authority;
- direction and non-coherent prepare/acquire semantics;
- submit/completion/drain/revoke ordering and process-teardown integration;
- real or faithful bounded DMA acceptance path and fault injection.

## Do not do

- no raw physical MMIO address ABI;
- no raw interrupt vector/controller ABI;
- no raw DMA pointer ABI;
- no app-visible IOMMU IDs;
- no ambient global device service authority over arbitrary memory;
- no assumption of coherent DMA;
- no universal driver DSL as a prerequisite.
