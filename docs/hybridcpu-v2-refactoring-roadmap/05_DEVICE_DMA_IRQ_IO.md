# Phase 5 — Device, MMIO, IRQ and DMA authority

## Status

**In progress — first device-authority root slice implemented.**

Phase 5 now has an exact capability-backed device lease lifecycle. MMIO mapping, IRQ routing and DMA grant/submit/completion remain separate later slices. The phase acceptance boundary is therefore **not** complete.

The cross-repository integration gate is pinned to HybridCPU neutral device-lease commit `e1c0255f3b7da7fa69e3230783b95b4521cc664c`, stacked on the Phase-4 neutral acquire owner `4dce496d072b56efae61dfa9d99058eaa782fea3`.

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

## Slice 1 — exact device lease authority root

The first Phase-5 slice deliberately stops before MMIO, IRQ and DMA. It establishes the device-side authority root that those later effects must compose with.

### Local admission

A process can request a device lease only from an exact local device capability and exact live platform domain binding:

```text
exact ProcessHandle generation
  + exact PlatformDomainBinding subject/generation
  + live CapabilityId
  + ResourceKind.Device
  + exact semantic ResourceId
  + requested Read / Write / Configure rights
  -> separate PlatformDeviceLease identity
```

Requested platform device rights are translated only to the corresponding local capability rights. A capability with insufficient rights, wrong resource kind, stale process generation or wrong platform-domain subject is rejected before provider device admission.

`CapabilityId` is admission authority. It is not a provider lease and is never passed through as external device authority.

### Separate identity spaces

The following identities are deliberately distinct:

```text
CapabilityId
PlatformDomainBindingId / generation
PlatformDeviceLeaseId / generation
PlatformProviderDomainLeaseId / generation
PlatformProviderDeviceLeaseId / generation
NeutralDomainBindingHandle / epoch
NeutralDeviceLeaseHandle / epoch
```

Provider and HybridCPU identities remain bridge/provider-private. The Sing-visible `PlatformDeviceLease` carries only the local lease identity, local platform-domain binding, semantic device resource and semantic rights.

### HybridCPU neutral device owner

The narrow `HybridCPU_NeutralRuntime` owner now materializes:

```text
NeutralDeviceLease(
    exact live NeutralDomainBindingLease,
    semantic NeutralDeviceIdentity,
    Read | Write | Configure)
```

It rejects invalid identities/rights, stale or revoked domains, and duplicate binding of the same semantic device in the same live domain lifetime. Exact close requires the materialized lease handle/epoch/domain/device/rights identity.

This export is intentionally only a semantic device lifetime. It exposes no MMIO register address, IRQ vector/controller route, DMA window, IOMMU binding/token, queue, lane, opcode, VM state or physical address.

### Revocation and teardown ordering

Device leases are synchronous lifetime authority in this slice; no asynchronous device work exists yet.

Normal explicit closure is:

```text
live local capability + PlatformDeviceLease
  -> RevokePlatformDevice
  -> exact provider device lease close
  -> exact HybridCPU neutral device lease close
  -> local device lease closed
  -> platform domain may close
```

Local capability revocation first marks the derived local lease unauthorized and closes the external device lease before returning success. An old lease cannot authorize later effects.

Process teardown includes the new authority class before platform-domain closure:

```text
Exiting
  -> close channels / revoke local capabilities
  -> close tracked device leases
  -> close borrow grants / region mappings
  -> close platform domain
  -> local process/domain/region reclaim
```

If provider device closure faults, teardown is fault-contained in `PlatformFaulted`; platform-domain close and local reclaim are forbidden.

The HybridCPU provider also independently refuses domain closure while one of its provider device leases remains live.

### Feature discovery

The real HybridCPU provider advertises:

```text
PlatformFeatureFamily.IoDomainBinding
PlatformDeviceLeaseContract.ContractVersion == 1
PlatformFeatureAvailability.Executable
```

For this slice, `IoDomainBinding` means only that the semantic device authority root is executable. It does **not** imply that DMA mapping, MMIO mapping or IRQ delivery is implemented. `PlatformFeatureFamily.DmaMapping` remains unavailable.

## Target provider contracts

### Device lease

The implemented bridge-private semantic lease is:

```text
DeviceLease BindDevice(
    PlatformDomainLease domain,
    LocalDeviceIdentity device,
    RequestedDeviceRights rights)
```

The Sing-visible device capability remains a local resource capability. The provider lease is never returned to the SIP client.

### MMIO

A future privileged device service may request:

```text
MmioMappingLease MapMmio(
    DeviceLease,
    registerRange,
    Read | Write)
```

The mapping must be bounded to the device resource/range. Ordinary apps must not get raw physical MMIO addresses.

### IRQ/event binding

A future slice should map a device interrupt source to a kernel-owned event route:

```text
IrqBindingLease BindInterrupt(DeviceLease, source, KernelEventEndpoint)
```

The provider handles vector/controller details. SIP clients receive normalized event/channel notifications, not raw interrupt vectors.

The contract must define enough semantics for edge/level acknowledgment and teardown so an IRQ cannot target a stale process generation.

### DMA

A later slice must use an exact grant derived from both device and region authority:

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

The audit found code-confirmed HybridCPU concepts for I/O domains, DMA windows, IOMMU bindings, permissions/ranges, domain epochs and explicit `NonCoherentFenceRequired` failure. These remain targets for later provider slices.

The first slice does not import those internal objects. Instead, a narrow neutral facade now owns only the exact semantic device lease lifetime required by SingNextOS.

SingNextOS should continue consuming stable exported semantic facades over hardware mechanisms. It should **not** import HybridCPU internal `IommuDomainBinding` or DMA authority objects into public/kernel contracts.

## Confused-deputy prevention

Bad shape:

```text
DriverService.MapForDma(address, length)
```

Required later shape:

```text
client supplies exact region capability/borrow
service supplies device capability
kernel validates both subjects/resources/generations
bridge materializes exact provider grant
```

Slice 1 establishes the second input — exact device authority — without fabricating the still-missing region+device DMA composition.

The service may choose protocol policy, but cannot widen the region or direction beyond caller-derived authority.

## DMA ownership state machine

Still future work. The target lifecycle remains:

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

Still future work. Do not add device-specific kernel syscalls. Reuse a small policy-neutral kernel event/completion primitive that can also support:

- timer wakeups;
- scheduler/runtime completions;
- accelerator completions;
- VM trap/event delivery.

Device service SIP translates those events into service-specific async APIs.

## Tests

Slice-1 focused tests prove:

- exact local `ResourceKind.Device` capability + live domain can bind a separate device lease;
- insufficient rights, wrong capability resource, wrong domain or stale process fail before provider device admission;
- malformed provider device/domain/rights/identity evidence fails closed and is best-effort revoked;
- an active device lease blocks early platform-domain closure;
- local capability revoke closes the derived external device authority;
- process termination closes device authority before the platform domain and local reclaim;
- provider closure fault pins process teardown and prevents domain close;
- real pinned HybridCPU runtime materializes and closes the exact neutral device lifetime;
- public Sing and neutral device surfaces expose no provider/hardware-shaped authority identities.

Later required negative tests remain:

- device capability without region authority cannot create DMA grant;
- region authority without device authority cannot create DMA grant;
- wrong DMA range/direction denied before provider submit;
- stale domain/IOMMU/provider epoch invalidates DMA grant;
- non-coherent device access without required prepare/fence denied;
- local capability revoke stops new submissions immediately while old DMA drains;
- process termination cannot reclaim buffer before DMA completion/revoke;
- stale IRQ binding cannot deliver to a recycled process generation;
- provider token never appears in a SIP payload.

## Acceptance criteria

Phase 5 is done only when one real or faithful provider path can perform a bounded DMA transfer over an owned region, survive denial/stale/revoke fault injection, and prove that the buffer is not reclaimed or returned to CPU ownership until device authority is closed.

**That acceptance criterion is not yet met. Phase 5 remains In progress.**

## Remaining Phase-5 work

- exact bounded MMIO lease/range semantics, if a stable external provider surface is available;
- IRQ/event binding with stale-generation-safe delivery and teardown;
- exact DMA grant composed from this device lease plus exact region authority;
- direction and non-coherent prepare/acquire semantics;
- submit/completion/drain/revoke ordering and process-teardown integration;
- real or faithful bounded DMA acceptance path and fault injection.

## Do not do

- no raw DMA pointer ABI;
- no app-visible IOMMU IDs;
- no ambient global device service authority over arbitrary memory;
- no assumption of coherent DMA;
- no universal driver DSL as a prerequisite;
- no raw IRQ vectors in high-level APIs.
