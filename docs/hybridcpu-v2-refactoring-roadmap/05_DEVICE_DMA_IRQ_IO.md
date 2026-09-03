# Phase 5 — Device, MMIO, IRQ and DMA authority

## Status

**In progress — two vertical slices implemented.**

Implemented slices:

1. exact capability-backed semantic device lease authority;
2. exact capability-backed bounded MMIO lease/range authority.

IRQ routing and DMA grant/submit/completion remain separate later slices. The Phase-5 acceptance boundary is therefore **not** complete.

The cross-repository integration gate for Slice 2 is pinned to HybridCPU neutral MMIO commit `eeda80b92e8f6733d950f43f65a50e55bde608df`, based on the merged device-lease stack head `be88202bfac9e372d2de2acf92fe3d783c3094cf`.

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

A driver with device authority must never be able to DMA into an arbitrary caller address.

## Slice 1 — exact device lease authority root

A process can materialize a semantic device lifetime only from:

```text
exact ProcessHandle generation
  + exact PlatformDomainBinding subject/generation
  + live CapabilityId
  + ResourceKind.Device
  + exact semantic ResourceId
  + requested Read / Write / Configure rights
  -> separate PlatformDeviceLease identity
```

The local capability is admission authority only. Provider and HybridCPU leases remain separate bridge-private identity spaces.

Revocation ordering is:

```text
revoke local authorization
  -> close dependent external device authority
  -> close platform domain
  -> local reclaim
```

Provider closure failure is fail-closed and pins teardown before domain closure/reclaim.

## Slice 2 — exact bounded MMIO lease/range

Slice 2 composes an exact live device lease with a separate local `ResourceKind.MmioRegion` capability. No raw physical address is accepted from the caller.

### Canonical MMIO capability identity

`CapabilityResourceIds.MmioRegion(...)` now encodes a canonical semantic authority tuple:

```text
semantic device resource id
+ semantic MMIO region resource id
+ authoritative semantic byte length
```

The runtime parses that tuple from the live local capability. The caller supplies only:

```text
offset
length
Read | Write
```

Therefore the caller cannot widen the authoritative region extent by passing a larger size parameter to `BindPlatformMmio`.

Admission requires:

```text
exact live ProcessHandle
+ exact live PlatformDeviceLease
+ live ResourceKind.MmioRegion CapabilityId
+ capability device id == device lease device id
+ CapabilityRights.Map
+ capability Read/Write rights matching requested access
+ device Configure right
+ device Read/Write rights matching requested access
+ exact bounded offset/length inside capability byte extent
```

All admission failures occur before provider MMIO materialization.

### Separate MMIO identity spaces

The MMIO lifetime keeps these identities distinct:

```text
CapabilityId
PlatformDeviceLeaseId / generation
PlatformMmioLeaseId / generation
PlatformProviderDeviceLeaseId / generation
PlatformProviderMmioLeaseId / generation
NeutralDeviceLeaseHandle / epoch
NeutralMmioLeaseHandle / epoch
```

The Sing-visible MMIO lease contains only:

```text
local MMIO lease identity
device lease
semantic region id + byte length
exact relative offset/length
semantic Read/Write access
```

It exposes no provider token, physical address, BAR number, PTE/page-table identity, interrupt vector, DMA window, IOMMU token, VM state, lane or opcode.

### HybridCPU neutral MMIO owner

The narrow `HybridCPU_NeutralRuntime` owner materializes:

```text
NeutralMmioLease(
    exact live NeutralDeviceLease,
    semantic region identity + byte length,
    exact relative range,
    Read | Write)
```

A mapping requires device `Configure` plus the matching Read/Write device rights. Invalid or overflowing ranges, invalid access, stale/forged device identities and duplicate live mapping of the same semantic MMIO region fail closed.

The first MMIO slice deliberately models exact authority/lifetime only. It does **not** export raw register addresses or implement a generic read/write syscall surface.

### Revocation / teardown ordering

An MMIO lease is synchronous authority in this slice; there is no asynchronous MMIO operation object.

Normal ordering is:

```text
MMIO live
  -> local MMIO authorization revoked
  -> exact provider MMIO close
  -> exact HybridCPU neutral MMIO close
  -> device may close
  -> platform domain may close
  -> local reclaim
```

Rules enforced by the slice:

- explicit device revoke drains all derived MMIO leases first;
- revoking an MMIO capability closes only MMIO authority derived from that capability;
- revoking the device capability drains dependent MMIO before device close;
- process teardown marks MMIO authorization revoked and closes MMIO before device/domain closure;
- MMIO provider-close failure pins teardown in `PlatformFaulted` and prevents device/domain close;
- the HybridCPU provider independently refuses device close while a provider MMIO lease remains live;
- the neutral runtime independently reports active MMIO dependents instead of silently closing the device underneath them;
- exact MMIO closure remains structurally valid after local device authorization is revoked, while any operation that would consume MMIO authority still requires live authorization.

### Feature discovery

The HybridCPU provider now advertises:

```text
PlatformFeatureFamily.IoDomainBinding    -> device lease contract v1 / Executable
PlatformFeatureFamily.MmioMapping        -> MMIO lease contract v1 / Executable
PlatformFeatureFamily.DmaMapping         -> Unavailable
```

`MmioMapping` was appended as a distinct feature family so existing feature-family numeric identities are not renumbered.

## IRQ/event binding — future slice

A later slice should map a semantic device interrupt source to a kernel-owned event route:

```text
IrqBindingLease BindInterrupt(DeviceLease, source, KernelEventEndpoint)
```

Provider vector/controller details must remain private. Delivery must be stale-generation-safe and teardown must prevent delivery into a recycled process generation.

## DMA — future slices

DMA must compose exact device authority with exact caller-derived region authority:

```text
DmaGrantLease BindDma(
    DeviceLease,
    PlatformRegionMappingLease,
    ExactRange,
    Direction)
```

Then the lifecycle remains:

```text
Prepare/Publish
  -> exact DMA grant
  -> Submit
  -> completion pending
  -> completion proven
  -> Acquire/maintenance
  -> revoke DMA grant
  -> revoke region mapping
  -> CPU ownership/reclaim allowed
```

No coherent-DMA assumption is permitted. Device-write and device-read ownership restrictions must be explicit.

## Tests

Slice-1 tests continue to prove exact device admission, separate identity, capability revocation, teardown ordering and real pinned HybridCPU device lifetime.

Slice-2 focused tests additionally prove:

- canonical MMIO capability identity round-trips device, region and authoritative extent;
- exact MMIO capability + exact live device lease materialize a separate bounded MMIO lease;
- wrong device resource, non-canonical MMIO capability, insufficient capability rights and out-of-range requests fail before provider mapping;
- device rights must include `Configure` plus requested Read/Write access;
- malformed provider MMIO identity/range fails closed and is best-effort revoked;
- MMIO capability revoke closes only derived MMIO authority;
- explicit device revoke closes MMIO before device;
- process teardown closes MMIO before device and platform domain;
- MMIO close fault pins teardown before device/domain closure;
- real pinned HybridCPU runtime materializes and closes the exact neutral MMIO lease;
- the prior device-surface negative test now permits this implemented MMIO facade while continuing to reject DMA/IRQ and hardware-shaped authority terms;
- public Sing and neutral MMIO surfaces contain no provider/hardware-shaped authority identities.

Later required negative tests remain:

- stale IRQ binding cannot deliver to a recycled process generation;
- device capability without region authority cannot create DMA grant;
- region authority without device authority cannot create DMA grant;
- wrong DMA range/direction denied before provider submit;
- stale domain/IOMMU/provider epoch invalidates DMA grant;
- non-coherent device access without required prepare/fence denied;
- local capability revoke stops new submissions immediately while old DMA drains;
- process termination cannot reclaim buffer before DMA completion/revoke;
- provider token never appears in a SIP payload.

## Acceptance criteria

Phase 5 is done only when one real or faithful provider path can perform a bounded DMA transfer over an owned region, survive denial/stale/revoke fault injection, and prove that the buffer is not reclaimed or returned to CPU ownership until device authority is closed.

**That acceptance criterion is not yet met. Phase 5 remains In progress.**

## Remaining Phase-5 work

- IRQ/event binding with stale-generation-safe delivery and teardown;
- exact DMA grant composed from device lease plus exact region authority;
- direction and non-coherent prepare/acquire semantics;
- submit/completion/drain/revoke ordering and process-teardown integration;
- real or faithful bounded DMA acceptance path and fault injection.

## Do not do

- no raw physical MMIO address ABI;
- no raw DMA pointer ABI;
- no app-visible IOMMU IDs;
- no ambient global device service authority over arbitrary memory;
- no assumption of coherent DMA;
- no universal driver DSL as a prerequisite;
- no raw IRQ vectors in high-level APIs.
