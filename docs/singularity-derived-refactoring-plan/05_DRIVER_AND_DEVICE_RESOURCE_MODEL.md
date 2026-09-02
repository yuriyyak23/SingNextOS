# Phase 5 — Driver and device resource model

## Goal

Carry forward Singularity's explicit I/O resource and driver lifecycle ideas using the bounded authority model already planned for HybridCPU Phase 5.

Historical `IoConfig`/`IoRange` and `IoIrq` are useful design references, but raw IRQ numbers and machine addresses are not the target public ABI.

## Driver principle

A driver is an isolated component/service. Being loaded as a driver does not grant ambient hardware authority.

A driver instance receives a bounded `DeviceResourceSet` such as:

```text
DeviceLease
MmioMappingLease[]
IoPortLease[]          // only where the platform exposes this concept
IrqBindingLease[]
DmaCapability / DmaGrant policy
```

All provider-side identities remain opaque and bridge-private.

## Authority intersection

A device effect is allowed only when the relevant authorities intersect:

```text
driver/device capability
AND exact caller/resource authority
AND live local generations
AND live platform leases
AND HybridCPU I/O-domain/IOMMU admission
```

A driver with device authority must not be able to widen a client-provided region, direction or operation class.

## MMIO

- only privileged device services may obtain MMIO mappings;
- mappings are exact to the granted register range and rights;
- ordinary applications never receive raw physical MMIO addresses;
- mapping lifetime is revocable and tied to device/domain generation.

## IRQ

Model an interrupt as a bound event source:

```text
physical/provider interrupt
-> IrqBindingLease
-> kernel-owned event/completion route
-> driver session
```

Controller/vector details remain provider-private. The semantic lifecycle must cover bind, wait/deliver, acknowledge when required, drain and release.

## DMA

DMA uses an exact region-derived grant and explicit visibility/completion lifecycle. The driver cannot request “map arbitrary address”.

```text
PrepareForConsumer
-> exact BindDma(range, direction)
-> Submit
-> completion
-> AcquireFromConsumer
-> revoke grant/mapping
```

## Driver crash/exit

Termination must:

1. stop new submissions;
2. close new client sessions;
3. cancel/drain in-flight requests according to protocol;
4. drain DMA/IRQ/platform operations;
5. revoke mappings and device leases;
6. reclaim local resources only after platform closure is proven.

## Tests

- device capability without region authority cannot DMA;
- region authority without device authority cannot DMA;
- range widening denied;
- stale device/domain/session generation denied;
- interrupt cannot target a recycled process generation;
- driver crash cannot reclaim an in-flight DMA buffer early;
- driver cannot expose provider tokens through SIP;
- non-coherent path fails closed without required visibility steps.

## Acceptance criteria

One driver vertical slice must prove bounded device authority, event delivery and DMA completion without ambient hardware access or raw platform identity leakage.
