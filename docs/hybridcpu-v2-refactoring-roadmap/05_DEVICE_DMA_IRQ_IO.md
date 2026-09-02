# Phase 5 — Device, MMIO, IRQ and DMA authority

## Status

**First hardware-I/O vertical slice.** Depends on Phases 1–4 and closes the highest-value part of `EXT-HCPU-002`/`EXT-HCPU-004`.

## Goal

Turn local `DmaCapability`, MMIO/IRQ capability types and owned regions into exact, revocable platform effects without giving drivers ambient authority.

The central rule is:

```text
client-derived exact region authority
AND device-service authority
AND live platform domain/mapping
AND HybridCPU I/O-domain/IOMMU admission
  -> bounded device effect
```

A driver with device authority must never be able to DMA into an arbitrary caller address.

## Target provider contracts

### Device lease

Introduce a bridge-private semantic lease:

```text
DeviceLease BindDevice(
    PlatformDomainLease domain,
    LocalDeviceIdentity device,
    RequestedDeviceRights rights)
```

The Sing-visible device capability remains a local resource capability. The provider lease is never returned to the SIP client.

### MMIO

A privileged device service may request:

```text
MmioMappingLease MapMmio(
    DeviceLease,
    registerRange,
    Read | Write)
```

The mapping is bounded to the device resource/range. Ordinary apps do not get raw physical MMIO addresses.

### IRQ/event binding

Map a device interrupt source to a kernel-owned event route:

```text
IrqBindingLease BindInterrupt(DeviceLease, source, KernelEventEndpoint)
```

The provider handles vector/controller details. SIP clients receive normalized event/channel notifications, not raw interrupt vectors.

The contract must define enough semantics for edge/level acknowledgment and teardown so an IRQ cannot target a stale process generation.

### DMA

Use an exact grant derived from both device and region authority:

```text
DmaGrantLease BindDma(
    DeviceLease,
    PlatformRegionMappingLease,
    ExactRange,
    Direction)
```

Then:

```text
PrepareForConsumer
Submit
Wait/Poll completion
AcquireFromConsumer
RevokeDma
RevokeRegionMapping
```

`Direction` must distinguish at least device-read, device-write and bidirectional where supported.

## Integration with HybridCPU-v2

The audit found code-confirmed HybridCPU concepts for I/O domains, DMA windows, IOMMU bindings, permissions/ranges, domain epochs and explicit `NonCoherentFenceRequired` failure. These are a strong target for the provider.

SingNextOS should consume a stable exported semantic facade over those mechanisms. It should **not** import HybridCPU internal `IommuDomainBinding` or DMA authority objects into public/kernel contracts.

## Confused-deputy prevention

Bad shape:

```text
DriverService.MapForDma(address, length)
```

Good shape:

```text
client supplies exact region capability/borrow
service supplies device capability
kernel validates both subjects/resources/generations
bridge materializes exact provider grant
```

The service may choose protocol policy, but cannot widen the region or direction beyond the caller-derived authority.

## DMA ownership state machine

Use a common lifecycle:

```text
CPU-Owned
  -> Prepare/Publish
Mapped-For-Device
  -> exact DMA grant
Device-Reading/Writing
  -> completion pending
Completed
  -> Acquire/maintenance
Draining
  -> revoke grant + IOTLB/mapping closure
CPU-Owned / transferred owner
```

For device-write buffers, CPU mutable access must remain unavailable until completion + acquire. For device-read buffers, app mutation must be prevented for the grant lifetime unless the protocol explicitly supports immutable snapshots.

## Interrupt and completion delivery

Do not add device-specific kernel syscalls. Reuse a small policy-neutral kernel event/completion primitive that can also support:

- timer wakeups;
- scheduler/runtime completions;
- accelerator completions;
- VM trap/event delivery.

Device service SIP translates those events into service-specific async APIs.

## Tests

Required negative tests:

- device capability without region authority cannot create DMA grant;
- region authority without device authority cannot create DMA grant;
- wrong range/direction denied before provider submit;
- stale domain/IOMMU/provider epoch invalidates grant;
- non-coherent device access without required prepare/fence denied;
- local capability revoke stops new submissions immediately while old DMA drains;
- process termination cannot reclaim buffer before DMA completion/revoke;
- stale IRQ binding cannot deliver to a recycled process generation;
- provider token never appears in a SIP payload.

## Acceptance criteria

Phase 5 is done when one real or faithful provider path can perform a bounded DMA transfer over an owned region, survive denial/stale/revoke fault injection, and prove that the buffer is not reclaimed or returned to CPU ownership until device authority is closed.

## Do not do

- no raw DMA pointer ABI;
- no app-visible IOMMU IDs;
- no ambient global device service authority over arbitrary memory;
- no assumption of coherent DMA;
- no universal driver DSL as a prerequisite;
- no raw IRQ vectors in high-level APIs.
